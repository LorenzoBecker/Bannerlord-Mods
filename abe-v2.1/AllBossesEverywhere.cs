using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using HarmonyLib;
using SemanticVersioning;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Match;
using SPTarkov.Server.Core.Models.Spt.Mod;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Services;
using Range = SemanticVersioning.Range;
using Version = SemanticVersioning.Version;

namespace AllBossesEverywhere;

public record AllBossesEverywhereMetadata : AbstractModMetadata
{
    public override string ModGuid { get; init; } = "com.lorenzobecker.allbosseseverywhere";
    public override string Name { get; init; } = "All Bosses Everywhere";
    public override string Author { get; init; } = "Lorenzo Becker";
    public override List<string>? Contributors { get; init; } = new() { "OpenAI" };
    public override Version Version { get; init; } = new("2.1.0");
    public override Range SptVersion { get; init; } = new("~4.0.13");
    public override List<string>? Incompatibilities { get; init; }
    public override Dictionary<string, Range>? ModDependencies { get; init; }
    public override string? Url { get; init; }
    public override bool? IsBundleMod { get; init; } = false;
    public override string License { get; init; } = "MIT";
}

[Injectable(TypePriority = OnLoadOrder.PostDBModLoader + 999)]
public sealed class AllBossesEverywhereMod : IOnLoad
{
    private readonly ISptLogger<AllBossesEverywhereMod> _logger;
    private readonly DatabaseService _databaseService;

    internal static ISptLogger<AllBossesEverywhereMod>? Logger;
    internal static ModConfig Config = ModConfig.CreateDefault();
    internal static readonly Dictionary<string, BossLocationSpawn> Templates = new(StringComparer.OrdinalIgnoreCase);
    internal static readonly Random Rng = new();

    public AllBossesEverywhereMod(ISptLogger<AllBossesEverywhereMod> logger, DatabaseService databaseService)
    {
        _logger = logger;
        _databaseService = databaseService;
    }

    public Task OnLoad()
    {
        Logger = _logger;
        LoadConfig();
        CacheBossTemplates();

        var harmony = new Harmony("com.lorenzobecker.allbosseseverywhere.v2.1");
        var original = AccessTools.Method(typeof(LocationLifecycleService), nameof(LocationLifecycleService.StartLocalRaid));
        var postfix = AccessTools.Method(typeof(StartLocalRaidPatch), nameof(StartLocalRaidPatch.Postfix));

        if (original is null || postfix is null)
        {
            _logger.Error("[All Bosses Everywhere] StartLocalRaid introuvable; patch non appliqué.");
            return Task.CompletedTask;
        }

        harmony.Patch(original, postfix: new HarmonyMethod(postfix));
        _logger.Success($"[All Bosses Everywhere] v2.1.0 chargé. {Templates.Count} modèles disponibles. Maximum additionnel: {Config.MaximumAdditionalBossesPerRaid}.");
        return Task.CompletedTask;
    }

    private void LoadConfig()
    {
        try
        {
            var directory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
            var path = Path.Combine(directory, "config.json");
            if (!File.Exists(path))
                File.WriteAllText(path, JsonSerializer.Serialize(ModConfig.CreateDefault(), JsonOptions.Write));

            Config = JsonSerializer.Deserialize<ModConfig>(File.ReadAllText(path), JsonOptions.Read) ?? ModConfig.CreateDefault();
            Config.Normalize(_logger);
        }
        catch (Exception ex)
        {
            _logger.Error($"[All Bosses Everywhere] Config invalide, valeurs par défaut utilisées: {ex}");
            Config = ModConfig.CreateDefault();
        }
    }

    private void CacheBossTemplates()
    {
        Templates.Clear();
        foreach (var bossConfig in Config.Bosses.Where(x => x.Enabled))
        {
            var location = _databaseService.GetLocation(bossConfig.SourceMap);
            var locationBase = GetLocationBase(location);
            var template = locationBase?.BossLocationSpawn?.FirstOrDefault(x => string.Equals(x.BossName, bossConfig.Role, StringComparison.OrdinalIgnoreCase));

            if (template is null)
            {
                _logger.Warning($"[All Bosses Everywhere] Modèle '{bossConfig.Role}' introuvable sur '{bossConfig.SourceMap}'.");
                continue;
            }

            Templates[bossConfig.Role] = DeepClone(template);
        }
    }

    private static LocationBase? GetLocationBase(object? location)
    {
        if (location is null) return null;
        return location.GetType().GetProperty("Base", BindingFlags.Public | BindingFlags.Instance)?.GetValue(location) as LocationBase;
    }

    internal static BossLocationSpawn DeepClone(BossLocationSpawn source)
    {
        var json = JsonSerializer.Serialize(source, JsonOptions.Clone);
        return JsonSerializer.Deserialize<BossLocationSpawn>(json, JsonOptions.Clone)
               ?? throw new InvalidOperationException("Échec du clonage profond BossLocationSpawn.");
    }
}

internal static class StartLocalRaidPatch
{
    public static void Postfix(StartLocalRaidRequestData request, StartLocalRaidResponseData __result)
    {
        try
        {
            var config = AllBossesEverywhereMod.Config;
            if (!config.Enabled || config.MaximumAdditionalBossesPerRaid <= 0 || __result?.LocationLoot is null)
                return;

            var map = NormalizeMapId(request.Location ?? __result.LocationLoot.Id);
            if (!config.Maps.TryGetValue(map, out var mapConfig) || !mapConfig.Enabled)
                return;
            if (!config.EnableLabs && map.Equals("laboratory", StringComparison.OrdinalIgnoreCase))
                return;

            var spawns = __result.LocationLoot.BossLocationSpawn ??= new List<BossLocationSpawn>();
            var alreadyPresent = spawns
                .Where(x => (x.BossChance ?? 0) > 0 && !string.IsNullOrWhiteSpace(x.BossName))
                .Select(x => x.BossName!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var candidates = config.Bosses
                .Where(x => x.Enabled)
                .Where(x => !alreadyPresent.Contains(x.Role))
                .Where(x => AllBossesEverywhereMod.Templates.ContainsKey(x.Role))
                .Select(x => new Candidate(x, config.GetEffectiveChance(map, x)))
                .Where(x => x.Chance > 0)
                .ToList();

            if (candidates.Count == 0)
            {
                if (config.DebugLogging)
                    AllBossesEverywhereMod.Logger?.Info($"[All Bosses Everywhere] {map}: aucun candidat éligible.");
                return;
            }

            var maxToAdd = Math.Min(config.MaximumAdditionalBossesPerRaid, candidates.Count);
            var added = 0;

            for (var slot = 0; slot < maxToAdd && candidates.Count > 0; slot++)
            {
                var totalChance = Math.Min(100.0, candidates.Sum(x => x.Chance));
                var roll = AllBossesEverywhereMod.Rng.NextDouble() * 100.0;
                if (roll >= totalChance)
                {
                    if (config.DebugLogging)
                        AllBossesEverywhereMod.Logger?.Info($"[All Bosses Everywhere] {map}: slot {slot + 1}, aucun boss (jet {roll:F2} / {totalChance:F2}%).");
                    break;
                }

                Candidate? selected = null;
                var cursor = 0.0;
                foreach (var candidate in candidates)
                {
                    cursor += candidate.Chance;
                    if (roll < cursor)
                    {
                        selected = candidate;
                        break;
                    }
                }
                if (selected is null) break;

                var zones = BuildZonePool(__result.LocationLoot, map, selected.Config.Role, config);
                if (zones.Count == 0)
                {
                    AllBossesEverywhereMod.Logger?.Warning($"[All Bosses Everywhere] {map}: aucune zone valide pour {selected.Config.DisplayName}; candidat ignoré.");
                    candidates.Remove(selected);
                    slot--;
                    continue;
                }

                var zone = zones[AllBossesEverywhereMod.Rng.Next(zones.Count)];
                var boss = AllBossesEverywhereMod.DeepClone(AllBossesEverywhereMod.Templates[selected.Config.Role]);
                boss.BossChance = 100;
                boss.BossZone = zone;
                boss.Time = selected.Config.SpawnTime;
                boss.IsRandomTimeSpawn = selected.Config.RandomTimeSpawn;
                boss.ForceSpawn = selected.Config.ForceSpawn;
                boss.IgnoreMaxBots = selected.Config.IgnoreMaxBots;
                boss.SptId = $"abe_v2_1_{selected.Config.Role}_{Guid.NewGuid():N}";

                spawns.Add(boss);
                alreadyPresent.Add(selected.Config.Role);
                candidates.Remove(selected);
                added++;

                AllBossesEverywhereMod.Logger?.Success($"[All Bosses Everywhere] {map}: {selected.Config.DisplayName} ajouté (chance {selected.Chance:F1} %, zone '{zone}', slot {added}/{maxToAdd}).");
            }

            if (config.DebugLogging)
                AllBossesEverywhereMod.Logger?.Info($"[All Bosses Everywhere] {map}: {added} boss additionnel(s) ajouté(s), maximum {config.MaximumAdditionalBossesPerRaid}.");
        }
        catch (Exception ex)
        {
            AllBossesEverywhereMod.Logger?.Error($"[All Bosses Everywhere] Erreur pendant la génération du raid: {ex}");
        }
    }

    private static List<string> BuildZonePool(LocationBase location, string map, string role, ModConfig config)
    {
        var openZones = (location.OpenZones ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (config.BossZoneOverrides.TryGetValue(map, out var byBoss)
            && byBoss.TryGetValue(role, out var specific)
            && specific.Count > 0)
            return ValidateExplicitZones(specific, openZones, map, role, config);

        if (config.Maps.TryGetValue(map, out var mapConfig) && mapConfig.GeneralZones.Count > 0)
            return ValidateExplicitZones(mapConfig.GeneralZones, openZones, map, role, config);

        if (map is "factory4_day" or "factory4_night")
            return new List<string> { "BotZone" };

        var excluded = mapConfig?.ExcludedZones ?? new List<string>();
        return openZones
            .Where(x => !config.ExcludedZoneNameFragments.Any(fragment => !string.IsNullOrWhiteSpace(fragment) && x.Contains(fragment, StringComparison.OrdinalIgnoreCase)))
            .Where(x => !excluded.Contains(x, StringComparer.OrdinalIgnoreCase))
            .ToList();
    }

    private static List<string> ValidateExplicitZones(IEnumerable<string> requested, List<string> openZones, string map, string role, ModConfig config)
    {
        var result = new List<string>();
        foreach (var zone in requested.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var excluded = config.Maps.TryGetValue(map, out var mapConfig)
                           && mapConfig.ExcludedZones.Contains(zone, StringComparer.OrdinalIgnoreCase);
            if (excluded)
            {
                AllBossesEverywhereMod.Logger?.Warning($"[All Bosses Everywhere] {map}/{role}: zone '{zone}' exclue par la configuration.");
                continue;
            }

            var factoryBotZone = map is "factory4_day" or "factory4_night"
                                 && zone.Equals("BotZone", StringComparison.OrdinalIgnoreCase);
            if (!factoryBotZone && openZones.Count > 0 && !openZones.Contains(zone, StringComparer.OrdinalIgnoreCase))
            {
                AllBossesEverywhereMod.Logger?.Warning($"[All Bosses Everywhere] {map}/{role}: zone '{zone}' absente de OpenZones; elle est ignorée.");
                continue;
            }
            result.Add(zone);
        }
        return result;
    }

    private static string NormalizeMapId(string? value)
    {
        var map = (value ?? string.Empty).Trim().ToLowerInvariant();
        return map switch
        {
            "customs" => "bigmap",
            "factory" => "factory4_day",
            "reserve" => "rezervbase",
            "streets" => "tarkovstreets",
            "groundzero" => "sandbox",
            "labs" => "laboratory",
            _ => map
        };
    }

    private sealed record Candidate(BossConfig Config, double Chance);
}

public sealed class ModConfig
{
    public bool Enabled { get; set; } = true;
    public bool EnableLabs { get; set; } = false;
    public bool DebugLogging { get; set; } = true;
    public int MaximumAdditionalBossesPerRaid { get; set; } = 1;
    public List<string> ExcludedZoneNameFragments { get; set; } = new();
    public Dictionary<string, MapConfig> Maps { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, Dictionary<string, List<string>>> BossZoneOverrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<BossConfig> Bosses { get; set; } = new();

    public double GetEffectiveChance(string map, BossConfig boss)
    {
        if (!Maps.TryGetValue(map, out var mapConfig) || !mapConfig.Enabled)
            return 0;
        return mapConfig.Bosses.TryGetValue(boss.Role, out var value) ? Math.Clamp(value, 0, 100) : 0;
    }

    public static ModConfig CreateDefault()
    {
        var roles = new[] { "bossBully", "bossKilla", "bossTagilla", "bossKojaniy", "bossSanitar", "bossKolontay", "bossPartisan", "bossGluhar", "bossBoar", "bossKnight" };
        var maps = new Dictionary<string, MapConfig>(StringComparer.OrdinalIgnoreCase);
        foreach (var map in new[] { "bigmap", "factory4_day", "factory4_night", "interchange", "lighthouse", "rezervbase", "sandbox", "sandbox_high", "shoreline", "tarkovstreets", "woods", "labyrinth" })
        {
            maps[map] = new MapConfig
            {
                Enabled = true,
                Bosses = roles.ToDictionary(role => role, role => role is "bossGluhar" or "bossBoar" or "bossKnight" ? 2.0 : 4.0, StringComparer.OrdinalIgnoreCase)
            };
        }

        return new ModConfig
        {
            Enabled = true,
            EnableLabs = false,
            DebugLogging = true,
            MaximumAdditionalBossesPerRaid = 1,
            ExcludedZoneNameFragments = new() { "snip", "sniper", "marksman" },
            Maps = maps,
            Bosses = new()
            {
                new("bossBully", "Reshala", "bigmap"), new("bossKilla", "Killa", "interchange"),
                new("bossTagilla", "Tagilla", "factory4_day"), new("bossKojaniy", "Shturman", "woods"),
                new("bossSanitar", "Sanitar", "shoreline"), new("bossKolontay", "Kollontay", "tarkovstreets"),
                new("bossPartisan", "Partisan", "bigmap"), new("bossGluhar", "Glukhar", "rezervbase"),
                new("bossBoar", "Kaban", "tarkovstreets"), new("bossKnight", "Goons", "lighthouse")
            }
        };
    }

    public void Normalize(ISptLogger<AllBossesEverywhereMod> logger)
    {
        ExcludedZoneNameFragments ??= new();
        Maps ??= new(StringComparer.OrdinalIgnoreCase);
        BossZoneOverrides ??= new(StringComparer.OrdinalIgnoreCase);
        Bosses ??= new();
        MaximumAdditionalBossesPerRaid = Math.Clamp(MaximumAdditionalBossesPerRaid, 0, 20);

        var validRoles = Bosses.Where(x => !string.IsNullOrWhiteSpace(x.Role)).Select(x => x.Role).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var boss in Bosses)
            if (string.IsNullOrWhiteSpace(boss.Role) || string.IsNullOrWhiteSpace(boss.SourceMap))
                logger.Warning("[All Bosses Everywhere] Entrée boss incomplète dans config.json.");

        foreach (var mapEntry in Maps)
        {
            mapEntry.Value.Bosses ??= new(StringComparer.OrdinalIgnoreCase);
            mapEntry.Value.GeneralZones ??= new();
            mapEntry.Value.ExcludedZones ??= new();
            foreach (var role in mapEntry.Value.Bosses.Keys.ToList())
            {
                mapEntry.Value.Bosses[role] = Math.Clamp(mapEntry.Value.Bosses[role], 0, 100);
                if (!validRoles.Contains(role))
                    logger.Warning($"[All Bosses Everywhere] {mapEntry.Key}: boss inconnu '{role}' dans la matrice.");
            }
        }

        foreach (var mapEntry in BossZoneOverrides)
            foreach (var roleEntry in mapEntry.Value)
            {
                if (!validRoles.Contains(roleEntry.Key))
                    logger.Warning($"[All Bosses Everywhere] {mapEntry.Key}: boss inconnu '{roleEntry.Key}' dans bossZoneOverrides.");
                roleEntry.Value ??= new();
            }
    }
}

public sealed class MapConfig
{
    public bool Enabled { get; set; } = true;
    public Dictionary<string, double> Bosses { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> GeneralZones { get; set; } = new();
    public List<string> ExcludedZones { get; set; } = new();
}

public sealed class BossConfig
{
    public BossConfig() { }
    public BossConfig(string role, string displayName, string sourceMap)
    {
        Role = role;
        DisplayName = displayName;
        SourceMap = sourceMap;
    }

    public string Role { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string SourceMap { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public int SpawnTime { get; set; } = -1;
    public bool RandomTimeSpawn { get; set; } = false;
    public bool ForceSpawn { get; set; } = false;
    public bool IgnoreMaxBots { get; set; } = false;
}

internal static class JsonOptions
{
    public static readonly JsonSerializerOptions Read = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public static readonly JsonSerializerOptions Write = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    public static readonly JsonSerializerOptions Clone = new()
    {
        PropertyNameCaseInsensitive = true,
        IncludeFields = true
    };
}
