# Relic Share

A Slay the Spire 2 mod that fixes multiplayer chest relics so ties feel fair instead of random.

## What it does

- **One extra relic choice per chest.** In multiplayer, treasure rooms now offer one more relic than there are players, so there's always a spare option left after everyone who wants a relic has taken one.
- **Real re-picks on a tie, not a random consolation prize.** If two or more players pick the same relic, it's resolved with a genuine fight - the loser(s) then get an actual second choice among what's left, instead of the game silently auto-assigning them whatever's left over.
- **The fight animation plays when the tie happens**, before the loser gets to pick again - not after, so it doesn't spoil the loser's second choice.
- **An already-resolved player can't accidentally take a second turn.** Once you've got your relic, clicking another one does nothing - only players still picking are able to.
- Singleplayer, and solo "fake multiplayer" runs, are completely untouched.

## Requirements

**Every player in the lobby needs this mod installed.** It changes a shared, synced value (how many relics a chest offers), so if only some clients have it, those clients' relic draws will disagree with everyone else's.

## Settings

If you have [ModConfig](https://www.nexusmods.com/slaythespire2/mods/27) installed, Relic Share adds a toggle to Settings → Mod Settings so you can turn it off without deleting any files (handy for a run with someone who doesn't have it installed). Without ModConfig, it's simply always on.

## Installation

Drop `RelicShare.dll` and `RelicShare.json` into your `mods/RelicShare/` folder (inside your Slay the Spire 2 install directory, next to `data_sts2_windows_x86_64/`). Both files are required - the `.json` is what lets the game discover the mod at all.

## Compatibility

Built against the game's current assemblies via Harmony patches on the native relic-picking code. If a game update changes that code significantly, the mod is designed to fail safely: it logs an error and simply doesn't apply its patches rather than affecting anything else, so it can't take down the game or your save even if it stops working on a new patch.
