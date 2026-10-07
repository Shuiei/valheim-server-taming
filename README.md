# ServerTaming

A server-only BepInEx plugin for Valheim that makes **Deer** and **Necks** tameable and breedable. Players don't need any mod, so it works with vanilla and crossplay (console) clients.

## How it works

- **Taming:** pen the animal and drop its food on the ground inside the pen, within `FeedRadius` (8 m). The animals don't walk to the food. A hungry animal eats one item, which keeps it fed for `FedMinutes` (5). While it's fed and not alerted, its taming time counts down. After `TamingMinutes` (25) it becomes tamed, using the vanilla "tamed" flag. Tamed deer stop running from players, and tamed necks stop attacking them.
- **Breeding:** a tamed animal that's fed and has a tamed partner of the same kind within `PartnerRange` (5 m) gains love points. After enough points it becomes pregnant, and after `PregnancyMinutes` (10) a **tamed adult** with the parent's stars appears. There are no fawns or custom creatures. Breeding stops once `MaxCreatures` (8) animals of that kind are within `PopulationRange` (15 m).
- **Feeding from chests:** animals can also eat from nearby chests, using the same rules as [ServersideQoL](https://thunderstore.io/c/valheim/p/ArgusMagnus/ServersideQoL/) TameAssist's `FeedFromContainers` (see below).
- **Messages:** players within `MessageRange` (30 m) see taming progress at 25/50/75%, plus messages when an animal is tamed or born.
- **Stars:** `MaxTameStars` sets the highest star count that can be tamed. The default, `-1`, means any, including the 3+ star creatures from level mods.

## ServersideQoL TameAssist compatibility

When ServersideQoL TameAssist is installed (and `UseTameAssistSettings` is on, which is the default), deer and necks follow TameAssist's settings instead of ServerTaming's own:

- **`FeedFromContainers`**, **`FeedFromContainersRange`** and **`FeedFromContainersLeaveAtLeast`** decide which chests the animals eat from, and how much food is always left in them.
- **Per-chest range from a sign:** a chest sign containing the `FeedFromContainersRangeSignPrefix` (🐗 by default) followed by a number, for example `🐗10`, sets that chest's range. It's capped at `FeedFromContainersMaxRange`. These are the chest signs that ServersideQoL ContainerSigns adds.
- **`TamingTimeMultiplier`** and **`FedDurationMultiplier`** are applied to `TamingMinutes` and `FedMinutes`.
- **`TamingProgressMessageType`**, for example `InWorld`, is used for taming progress, with the same "tameness" text as TameAssist.

Unlike TameAssist, which only feeds tamed animals from chests, ServerTaming also feeds wild deer and necks that are being tamed, because they can't walk to food. Set `FeedWildFromContainers = false` to turn that off.

Without TameAssist, set `[Containers] FeedFromContainersRange` (0 means off) and `LeaveAtLeast` in ServerTaming's own config.

Chests that are open are skipped. When food is taken from a chest, only that one item's bytes in the saved inventory are changed, so all the other items stay exactly as they were.

Default foods:

| Animal | Foods |
| --- | --- |
| Deer | Raspberry, Blueberries, Cloudberry, Carrot, Turnip, Onion, Mushroom, MushroomYellow |
| Neck | FishRaw |

All of these can be changed in `BepInEx/config/local.servertaming.cfg`. Each animal has its own section: `Enabled`, `Foods`, `TamingMinutes`, `FedMinutes`, `Breeding` and `PregnancyMinutes`.

## Limitations

Without a client mod there's nothing to add the vanilla taming component to each player's game, so:

- there's no petting, Follow/Stay or naming, and tamed animals simply wander (keep them fenced)
- animals don't walk to food, so put the food close to them
- taming and breeding only progress in areas loaded around connected players, the same as vanilla

## Admin command

With [ServerDevcommands](https://thunderstore.io/c/valheim/p/JereKuusela/Server_devcommands/) installed, admins can type `server tamestatus [radius]` in the F5 console. It lists nearby deer and necks with their stars, taming %, fed time, love points and pregnancy.

## Installation

Put `ServerTaming.dll` in `BepInEx/plugins/ServerTaming/` on the **dedicated server** only.

## Building

The project targets .NET Framework 4.7.2. It references the game and BepInEx assemblies from a Valheim dedicated server install with BepInEx. By default it looks two folders above the project; you can point it somewhere else:

```sh
dotnet build -c Release -p:ValheimDir=/path/to/valheim_server
```

## Technical notes

- **Progress storage:** progress is kept in server memory. Writing it into objects that players' games control would be lost to their next update. Just before the world saves, it's copied into the objects' saved data, and it's read back after a restart. This is needed because object IDs change on every world load.
- **Taming:** the server asks the player's game that controls the animal to run the vanilla `RPC_SetTamed`. If no player's game controls the animal, the server sets the flag directly.
- **Eating:** the server takes control of the food item, then lowers its stack or removes it.
- **Births:** the server creates the new adult directly, already marked as tamed. The nearest player's game takes control of it and shows a vanilla deer or neck.
