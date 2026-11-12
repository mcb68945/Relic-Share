using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Players;
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
    /// Round 1: in multiplayer, add one extra relic choice to the chest over the number of players, so a tie
    /// doesn't automatically mean someone gets left with nothing to choose between.
    /// </summary>
    [HarmonyPatch(typeof(TreasureRoomRelicSynchronizer), "BeginRelicPicking")]
    internal static class TreasureRoomRelicSynchronizer_BeginRelicPicking_Patch
    {
        private static readonly FieldInfo PlayerCollectionField = AccessTools.Field(typeof(TreasureRoomRelicSynchronizer), "_playerCollection");
        private static readonly FieldInfo RngField = AccessTools.Field(typeof(TreasureRoomRelicSynchronizer), "_rng");
        private static readonly FieldInfo SharedGrabBagField = AccessTools.Field(typeof(TreasureRoomRelicSynchronizer), "_sharedGrabBag");
        private static readonly FieldInfo CurrentRelicsField = AccessTools.Field(typeof(TreasureRoomRelicSynchronizer), "_currentRelics");
        private static readonly FieldInfo VotesChangedField = AccessTools.Field(typeof(TreasureRoomRelicSynchronizer), "VotesChanged");
        private static readonly MethodInfo RollRarityByRng = AccessTools.Method(typeof(RelicFactory), "RollRarity", new[] { typeof(Rng) });

        private const int MaxExtraRelics = 8;

        internal static void Postfix(TreasureRoomRelicSynchronizer __instance)
        {
            try
            {
                if (PlayerCollectionField.GetValue(__instance) is not IPlayerCollection playerCollection)
                {
                    return;
                }

                var players = playerCollection.Players;
                int playerCount = players.Count;
                if (playerCount <= 1 || CurrentRelicsField.GetValue(__instance) is not IList { Count: not 0 } currentRelics)
                {
                    return;
                }

                int targetCount = playerCount + 1;
                if (currentRelics.Count >= targetCount)
                {
                    return;
                }

                if (RngField.GetValue(__instance) is not Rng rng || SharedGrabBagField.GetValue(__instance) is not RelicGrabBag grabBag)
                {
                    Log.Info("[RelicShare] Couldn't reach the shared Rng/grab bag - skipping the extra relic choice.", 0);
                    return;
                }

                var runState = players.FirstOrDefault()?.RunState;
                if (runState == null) { return; }

                int added = 0;
                while (currentRelics.Count < targetCount && added < MaxExtraRelics)
                {
                    var rarity = (RelicRarity)RollRarityByRng.Invoke(null, new object[] { rng })!;
                    RelicModel relic = grabBag.PullFromFront(rarity, runState) ?? RelicFactory.FallbackRelic;
                    currentRelics.Add(relic);
                    added++;
                }

                if (added > 0)
                {
                    if (VotesChangedField.GetValue(__instance) is Action votesChanged)
                    {
                        votesChanged();
                    }
                    Log.Info($"[RelicShare] {playerCount} players in the party - added {added} extra relic choice(s), {currentRelics.Count} total offered.", 0);
                }
            }
            catch (Exception ex)
            {
                Log.Error($"[RelicShare] Failed to add extra relic choice(s): {ex}", 0);
            }
        }
    }
}
