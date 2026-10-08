# Changelog

## 1.1.6

- Fix runaway breeding: animals that were already pregnant gave birth even after `MaxCreatures` was
  reached, and animals that left the pen formed new small groups that each bred up to the limit.
  Pregnancies now count toward `MaxCreatures`, the limit is checked again at birth, and the new
  `[Breeding] MaxTamedNearby` (30, `0` = no limit) stops breeding once that many tamed animals of the
  kind are anywhere around players.

## 1.1.5

- `MaxCreatures` per animal: `[Deer] MaxCreatures` and `[Neck] MaxCreatures` (`-1`, the default, uses
  `[Breeding] MaxCreatures`).

## 1.1.4

- 3+ star animals keep their taming, fed, love and pregnancy progress when ServersideQoL
  CreatureLevelUp replaces them with a copy (it does this to show their stars and size).
- New `[General] DebugLog` option to log feeding and breeding decisions.
- The config file is now `Tie.ServerTaming.cfg` (plugin ID `Tie.ServerTaming`). An existing
  `local.servertaming.cfg` is renamed automatically.

## 1.1.3

- Offspring appear between the mother and her nearest tamed partner, so they stay inside the pen.

## 1.1.2

- Chest feeding reads chest inventories correctly; `tamestatus` lists feeding chests.

## 1.1.1

- The chest feed range (`🐗<n>`) is read from the chest's own text (ServersideQoL ContainerSigns).

## 1.1.0

- Feeding from chests, compatible with ServersideQoL TameAssist.

## 1.0.0

- First release: Deer and Necks can be tamed with food and bred into tamed adults, server-side only.
