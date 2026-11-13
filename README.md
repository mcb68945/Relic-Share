# Relic +1

*Formerly Relic Share. The mod id is still `RelicShare`, so co-op matching and existing installs keep working.*

A Slay the Spire 2 mod: every chest offers one more relic than there are players. Solo, you pick 1 of 2. In co-op there's always a spare, and ties get a real re-pick.

## What it does

- **Solo: pick 1 of 2.** Chests show two relics and you take one.
- **Co-op: one extra relic choice per chest.** Treasure rooms offer one more relic than there are players, so there's always a spare option left after everyone who wants a relic has taken one.
- **Runs it boosted are saved as custom runs.** It's a buff, so any run where it added a relic is written to your run history as a custom run, which community stats sites leave out.
- **Real re-picks on a tie, not a random consolation prize.** If two or more players pick the same relic, it's resolved with a genuine fight - the loser(s) then get an actual second choice among what's left, instead of the game silently auto-assigning them whatever's left over.
- **The fight animation plays when the tie happens**, before the loser gets to pick again - not after, so it doesn't spoil the loser's second choice.
- **An already-resolved player can't accidentally take a second turn.** Once you've got your relic, clicking another one does nothing - only players still picking are able to.
- Singleplayer, and solo "fake multiplayer" runs, are completely untouched.

## Requirements

**Every player in the lobby needs this mod installed.** It changes a shared, synced value (how many relics a chest offers), so if only some clients have it, those clients' relic draws will disagree with everyone else's.

## Settings

If you have [ModConfig](https://www.nexusmods.com/slaythespire2/mods/27) installed, Relic Share adds a toggle to Settings → Mod Settings so you can turn it off without deleting any files (handy for a run with someone who doesn't have it installed). Without ModConfig, it's simply always on.

## Installation

Put these four files in your `mods/RelicShare/` folder (inside your Slay the Spire 2 install directory, next to `data_sts2_windows_x86_64/`):

- `RelicShare.dll`
- `RelicShare.V111.dll`
- `RelicShare.V113.dll`
- `RelicShare.json`

All four are required. The `.json` is what lets the game find the mod at all.

## Game versions

Works on both game v0.111 and v0.113+ (the live and beta branches as of November 2026). `RelicShare.dll` is a small loader: at startup it checks which version of the game's chest code is running and loads `RelicShare.V111.dll` or `RelicShare.V113.dll`.

- **v0.113+** chests natively support several picking rounds (added for Blitz mode). Relic Share builds its tie re-pick on that: the fight plays at the tie, then only the loser picks again.
- **v0.111** uses the older approach, which manages the re-pick itself.

If a game update changes the chest code in a way the mod doesn't recognise, it logs an error and leaves the game's own behavior alone.

## Building from source

1. Install the .NET 9 SDK.
2. Copy `sts2.dll`, `0Harmony.dll` and `GodotSharp.dll` from each game version's `data_sts2_windows_x86_64/` into `lib/v111/` and `lib/v113/`. They aren't in this repo because they belong to the game.
3. Run `./build.sh`. The three DLLs land in `out/`.

Source layout: `Loader.cs` (the loader), `v111/` and `v113/` (one build per game version), `shared/` (ModConfig toggle, used by both).
