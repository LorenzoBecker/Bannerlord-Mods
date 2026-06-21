# All Bosses Everywhere 2.1.0 — SPT 4.0.13

## Installation
Ferme le jeu et le serveur, puis fusionne le dossier `SPT` de l'archive avec la racine de ton installation.

## Nouveautés 2.1
- Matrice complète et lisible `maps -> bosses -> chance`.
- Zones spécifiques par boss et par carte via `bossZoneOverrides`.
- Priorité des zones : override boss -> zones générales de la carte -> `OpenZones` valides.
- Validation des zones explicites avec avertissement si une zone n'existe pas dans `OpenZones`.
- Les zones sniper restent exclues automatiquement pour le fallback général.
- Le clonage complet des boss et de leurs escortes est conservé.
- Les spawns vanilla et ceux ajoutés par les autres mods ne sont jamais supprimés.

## Matrice des chances
Chaque carte contient toutes les chances effectives :

```json
"bigmap": {
  "enabled": true,
  "bosses": {
    "bossBully": 0,
    "bossKilla": 4,
    "bossBoar": 2
  }
}
```

Une valeur `0` interdit au mod d'ajouter ce boss sur cette carte. Les boss déjà présents naturellement sont de toute façon exclus au lancement du raid.

## Zones spécifiques par boss

```json
"bossZoneOverrides": {
  "bigmap": {
    "bossBoar": ["ZoneDormitory", "ZoneGasStation"],
    "bossKilla": ["ZoneScavBase"]
  }
}
```

Une liste vide signifie : aucune restriction spécifique, le mod utilise le fallback normal.

## Zones générales d'une carte

```json
"bigmap": {
  "generalZones": ["ZoneDormitory", "ZoneGasStation"],
  "excludedZones": []
}
```

Ces zones s'appliquent à tous les boss de la carte qui ne disposent pas d'un override spécifique.

## Ordre de priorité
1. `bossZoneOverrides[map][boss]`
2. `maps[map].generalZones`
3. `OpenZones` de la carte, nettoyées des zones sniper et des zones exclues

## Maximum de boss additionnels

```json
"maximumAdditionalBossesPerRaid": 1
```

`1` reste le réglage recommandé. Une valeur plus haute autorise plusieurs boss additionnels, sans doublon du même rôle durant le même raid.
