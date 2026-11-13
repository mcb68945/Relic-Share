using System;
using System.Collections.Generic;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Relics;
using MegaCrit.Sts2.Core.Factories;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Runs;

namespace RelicShare
{
    /// <summary>
    /// Top the chest up to one more relic than there are players (solo: pick 1 of 2), so a tie never leaves the
    /// loser with nothing to choose between. Game v0.113+ shape: the synchronizer holds the run state in
    /// <c>_runState</c> (it used to be <c>_playerCollection</c>). Co-op Blitz already adds two extra relics
    /// natively, so this adds nothing there.
    /// </summary>
    [HarmonyPatch(typeof(TreasureRoomRelicSynchronizer), nameof(TreasureRoomRelicSynchronizer.BeginRelicPicking))]
    internal static class BeginRelicPicking_Patch
    {
        private const int MaxExtraRelics = 8;

        internal static void Postfix(TreasureRoomRelicSynchronizer __instance)
        {
            try
            {
                Chest.Begin(__instance);
                // Co-op ties are handled by TieRounds (multiplayer only); the extra choice applies solo too.
                if (RunManager.Instance == null) { return; }

                IRunState runState = Sync.RunState(__instance);
                List<RelicModel>? relics = Sync.CurrentRelics(__instance);
                if (relics == null || relics.Count == 0) { return; }

                int playerCount = runState.Players.Count;
                int target = playerCount + 1;
                Rng rng = Sync.Rng(__instance);
                RelicGrabBag bag = Sync.GrabBag(__instance);

                int added = 0;
                while (relics.Count < target && added < MaxExtraRelics)
                {
                    RelicRarity rarity = RelicFactory.RollRarity(rng);
                    relics.Add(bag.PullFromFront(rarity, runState) ?? RelicFactory.FallbackRelic);
                    added++;
                }

                if (added > 0)
                {
                    RunMarker.Boosted(runState);
                    Sync.FireVotesChanged(__instance);
                    Log.Info($"[RelicShare] {playerCount} players in the party - added {added} extra relic choice(s), {relics.Count} total offered.", 0);
                }
            }
            catch (Exception ex)
            {
                Log.Error($"[RelicShare] Failed to add extra relic choice(s): {ex}", 0);
            }
        }
    }
}
