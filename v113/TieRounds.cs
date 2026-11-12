using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.TreasureRelicPicking;
using MegaCrit.Sts2.Core.Extensions;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes.Screens.TreasureRoomRelic;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Runs;

namespace RelicShare
{
    // Game v0.113 added native support for several picking rounds per chest (TreasureRoomRelicSynchronizer._roundsLeft,
    // used by Blitz). Between rounds the UI removes the awarded relics, renumbers the rest and re-enables picking, and
    // the room only closes after the round flagged as final. Relic Share's tie re-pick is built on that: when a tie
    // leaves the loser(s) with a real choice, AwardRelics resolves the round itself, keeps the chest open for one more
    // native round, and only the still-needy players vote again. The fight animation plays at the tie, natively.

    /// <summary>Private members of TreasureRoomRelicSynchronizer this mod reads or writes.</summary>
    internal static class Sync
    {
        private static readonly AccessTools.FieldRef<TreasureRoomRelicSynchronizer, IRunState> RunStateRef =
            AccessTools.FieldRefAccess<TreasureRoomRelicSynchronizer, IRunState>("_runState");
        private static readonly AccessTools.FieldRef<TreasureRoomRelicSynchronizer, List<RelicModel>?> CurrentRelicsRef =
            AccessTools.FieldRefAccess<TreasureRoomRelicSynchronizer, List<RelicModel>?>("_currentRelics");
        private static readonly AccessTools.FieldRef<TreasureRoomRelicSynchronizer, List<TreasureRoomRelicSynchronizer.PlayerVote>> VotesRef =
            AccessTools.FieldRefAccess<TreasureRoomRelicSynchronizer, List<TreasureRoomRelicSynchronizer.PlayerVote>>("_votes");
        private static readonly AccessTools.FieldRef<TreasureRoomRelicSynchronizer, Rng> RngRef =
            AccessTools.FieldRefAccess<TreasureRoomRelicSynchronizer, Rng>("_rng");
        private static readonly AccessTools.FieldRef<TreasureRoomRelicSynchronizer, RelicGrabBag> GrabBagRef =
            AccessTools.FieldRefAccess<TreasureRoomRelicSynchronizer, RelicGrabBag>("_sharedGrabBag");
        private static readonly AccessTools.FieldRef<TreasureRoomRelicSynchronizer, int> RoundsLeftRef =
            AccessTools.FieldRefAccess<TreasureRoomRelicSynchronizer, int>("_roundsLeft");
        private static readonly AccessTools.FieldRef<TreasureRoomRelicSynchronizer, Action?> VotesChangedRef =
            AccessTools.FieldRefAccess<TreasureRoomRelicSynchronizer, Action?>("VotesChanged");
        private static readonly AccessTools.FieldRef<TreasureRoomRelicSynchronizer, Action<List<RelicPickingResult>, bool>?> RelicsAwardedRef =
            AccessTools.FieldRefAccess<TreasureRoomRelicSynchronizer, Action<List<RelicPickingResult>, bool>?>("RelicsAwarded");
        private static readonly MethodInfo LocalPlayerGetter =
            AccessTools.PropertyGetter(typeof(TreasureRoomRelicSynchronizer), "LocalPlayer");

        internal static IRunState RunState(TreasureRoomRelicSynchronizer s) => RunStateRef(s);
        internal static List<RelicModel>? CurrentRelics(TreasureRoomRelicSynchronizer s) => CurrentRelicsRef(s);
        internal static List<TreasureRoomRelicSynchronizer.PlayerVote> Votes(TreasureRoomRelicSynchronizer s) => VotesRef(s);
        internal static Rng Rng(TreasureRoomRelicSynchronizer s) => RngRef(s);
        internal static RelicGrabBag GrabBag(TreasureRoomRelicSynchronizer s) => GrabBagRef(s);
        internal static void SetRoundsLeft(TreasureRoomRelicSynchronizer s, int n) => RoundsLeftRef(s) = n;
        internal static Player? LocalPlayer(TreasureRoomRelicSynchronizer s) => LocalPlayerGetter.Invoke(s, null) as Player;
        internal static void FireVotesChanged(TreasureRoomRelicSynchronizer s) => VotesChangedRef(s)?.Invoke();
        internal static void FireRelicsAwarded(TreasureRoomRelicSynchronizer s, List<RelicPickingResult> results, bool isFinished) =>
            RelicsAwardedRef(s)?.Invoke(results, isFinished);
    }

    /// <summary>Per-chest state. Reset whenever a chest starts.</summary>
    internal static class Chest
    {
        private static TreasureRoomRelicSynchronizer? _sync;

        /// <summary>Players who are done with this chest (got a relic, or skipped).</summary>
        internal static readonly HashSet<ulong> Resolved = new();

        /// <summary>Players who got a relic in an earlier round of this chest.</summary>
        internal static readonly HashSet<ulong> ResolvedWithRelic = new();

        internal static int RepickRounds;

        internal static void Begin(TreasureRoomRelicSynchronizer sync)
        {
            _sync = sync;
            Resolved.Clear();
            ResolvedWithRelic.Clear();
            RepickRounds = 0;
            HolderRemoval.Clear();
        }

        /// <summary>Real multiplayer (not solo, not fake multiplayer) and a chest this mod saw start.</summary>
        internal static bool Applies(TreasureRoomRelicSynchronizer sync)
        {
            if (!ReferenceEquals(sync, _sync)) { return false; }
            var rm = RunManager.Instance;
            if (rm == null || rm.IsSingleplayerOrFakeMultiplayer) { return false; }
            return Sync.RunState(sync).Players.Count > 1;
        }
    }

    /// <summary>
    /// Replaces native AwardRelics for a round where a tie would leave its loser(s) more than one relic to choose
    /// from. Every other round (no tie, or nothing left worth choosing between) goes to native code, which then
    /// finishes the chest exactly as vanilla does (consolation prizes, skipped leftovers).
    /// Runs identically on every client from the same votes and the same shared Rng, so clients stay in sync.
    /// </summary>
    [HarmonyPatch(typeof(TreasureRoomRelicSynchronizer), "AwardRelics")]
    internal static class AwardRelics_Patch
    {
        internal static bool Prefix(TreasureRoomRelicSynchronizer __instance)
        {
            try
            {
                if (!Chest.Applies(__instance)) { return true; }

                IReadOnlyList<Player> players = Sync.RunState(__instance).Players;
                List<RelicModel>? relics = Sync.CurrentRelics(__instance);
                List<TreasureRoomRelicSynchronizer.PlayerVote> votes = Sync.Votes(__instance);
                if (relics == null || votes.Count != players.Count) { return true; }

                // Votes of players still picking this chest, grouped by relic.
                var groups = new SortedDictionary<int, List<Player>>();
                for (int j = 0; j < players.Count; j++)
                {
                    if (Chest.Resolved.Contains(players[j].NetId)) { continue; }
                    if (votes[j].index is int i && i >= 0 && i < relics.Count)
                    {
                        if (!groups.TryGetValue(i, out var list)) { groups[i] = list = new List<Player>(); }
                        list.Add(players[j]);
                    }
                }

                int losers = groups.Values.Sum(g => g.Count - 1);
                int leftAfter = relics.Count - groups.Count;
                if (losers == 0 || leftAfter <= losers)
                {
                    // No tie, or no real choice left for the losers: native finishes the chest.
                    if (Chest.RepickRounds > 0)
                    {
                        Log.Info($"[RelicShare] Tie re-pick finished after {Chest.RepickRounds} extra round(s).", 0);
                    }
                    return true;
                }

                // Resolve this round like native does, minus consolation prizes: losers pick again instead.
                Rng rng = Sync.Rng(__instance);
                RelicPickingFightMove[] moves = Enum.GetValues<RelicPickingFightMove>();
                var results = new List<RelicPickingResult>();
                foreach (var (index, voters) in groups)
                {
                    RelicPickingResult result = voters.Count == 1
                        ? new RelicPickingResult
                        {
                            type = RelicPickingResultType.OnlyOnePlayerVoted,
                            relic = relics[index],
                            relicIndex = index,
                            player = voters[0],
                        }
                        : RelicPickingResult.GenerateRelicFight(voters, relics[index], index, () => rng.NextItem(moves));
                    results.Add(result);
                }

                foreach (RelicPickingResult r in results)
                {
                    Chest.Resolved.Add(r.player!.NetId);
                    Chest.ResolvedWithRelic.Add(r.player!.NetId);
                }
                for (int j = 0; j < players.Count; j++)
                {
                    // A still-picking player who skipped this round is done too.
                    if (votes[j].voteReceived && !votes[j].index.HasValue) { Chest.Resolved.Add(players[j].NetId); }
                }

                // Same bookkeeping native does between rounds: drop the awarded relics (by index, highest first,
                // so duplicate relic models can't remove the wrong one) and keep the chest open one more round.
                foreach (int index in results.Select(r => r.relicIndex).OrderByDescending(i => i))
                {
                    relics.RemoveAt(index);
                }
                Sync.SetRoundsLeft(__instance, 2); // native OnPicked decrements this to 1 right after we return

                // Animate this round now (fight included), before anyone picks again.
                Sync.FireRelicsAwarded(__instance, results, false);

                // Next round: done players count as voted, losers vote again.
                int stillPicking = 0;
                for (int j = 0; j < players.Count; j++)
                {
                    bool done = Chest.Resolved.Contains(players[j].NetId);
                    votes[j].index = null;
                    votes[j].voteReceived = done;
                    if (!done) { stillPicking++; }
                }
                Sync.FireVotesChanged(__instance);

                Chest.RepickRounds++;
                Log.Info($"[RelicShare] Tie on a relic - {stillPicking} player(s) choosing again between {relics.Count} leftover relic(s) instead of getting one auto-assigned.", 0);
                return false;
            }
            catch (Exception ex)
            {
                Log.Error($"[RelicShare] Tie re-pick failed, falling back to the game's own relic awarding: {ex}", 0);
                return true;
            }
        }
    }

    /// <summary>A player who's done with this chest can't vote again during someone else's re-pick.</summary>
    [HarmonyPatch(typeof(TreasureRoomRelicSynchronizer), nameof(TreasureRoomRelicSynchronizer.PickRelicLocally))]
    internal static class PickRelicLocally_Patch
    {
        internal static bool Prefix(TreasureRoomRelicSynchronizer __instance)
        {
            try
            {
                if (Chest.Resolved.Count == 0 || !Chest.Applies(__instance)) { return true; }
                Player? me = Sync.LocalPlayer(__instance);
                if (me != null && Chest.Resolved.Contains(me.NetId))
                {
                    Log.Info("[RelicShare] Ignored a pick from a player who's already done with this chest.", 0);
                    return false;
                }
            }
            catch (Exception ex)
            {
                Log.Error($"[RelicShare] PickRelicLocally check failed: {ex}", 0);
            }
            return true;
        }
    }

    /// <summary>
    /// Native shows a "skipped" hand for anyone whose vote is received-but-empty. During a re-pick that describes
    /// players who already won a relic, which would read as if they'd passed - keep their hand as it was.
    /// </summary>
    [HarmonyPatch(typeof(NHandImage), nameof(NHandImage.SetSkipped))]
    internal static class SetSkipped_Patch
    {
        internal static bool Prefix(NHandImage __instance)
        {
            Player? p = __instance.Player;
            return p == null || !Chest.ResolvedWithRelic.Contains(p.NetId);
        }
    }

    /// <summary>
    /// Native between-round cleanup removes awarded holders with <c>_holdersInUse.RemoveAt(result.relicIndex)</c>,
    /// one result at a time in animation order. That's only correct when a round awards one relic (all native ever
    /// does between rounds); with two awarded in one round it removes the wrong holder or throws, which would leave
    /// the chest stuck. Defer those removals and apply them highest-index-first in RefreshNavigation, which native
    /// calls right after.
    /// </summary>
    internal static class HolderRemoval
    {
        private static readonly Dictionary<List<NTreasureRoomRelicHolder>, List<int>> Pending = new();

        public static void Defer(List<NTreasureRoomRelicHolder> holders, int index)
        {
            if (!Pending.TryGetValue(holders, out var list)) { Pending[holders] = list = new List<int>(); }
            list.Add(index);
        }

        internal static void Clear() => Pending.Clear();

        internal static void Apply(List<NTreasureRoomRelicHolder> holders)
        {
            if (!Pending.Remove(holders, out var indices)) { return; }
            foreach (int i in indices.Distinct().OrderByDescending(i => i))
            {
                if (i >= 0 && i < holders.Count) { holders.RemoveAt(i); }
            }
            for (int i = 0; i < holders.Count; i++) { holders[i].Index = i; }
        }
    }

    [HarmonyPatch]
    internal static class AnimateRelicAwards_DeferRemoval_Patch
    {
        internal static MethodBase TargetMethod()
        {
            Type stateMachine = typeof(NTreasureRoomRelicCollection)
                .GetNestedTypes(AccessTools.all)
                .First(t => t.Name.Contains("AnimateRelicAwards"));
            return AccessTools.Method(stateMachine, "MoveNext");
        }

        internal static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo removeAt = AccessTools.Method(typeof(List<NTreasureRoomRelicHolder>), nameof(List<NTreasureRoomRelicHolder>.RemoveAt));
            MethodInfo defer = AccessTools.Method(typeof(HolderRemoval), nameof(HolderRemoval.Defer));
            int replaced = 0;
            foreach (CodeInstruction ins in instructions)
            {
                if (ins.Calls(removeAt))
                {
                    ins.opcode = OpCodes.Call;
                    ins.operand = defer;
                    replaced++;
                }
                yield return ins;
            }
            if (replaced != 1)
            {
                Log.Warn($"[RelicShare] Expected 1 holder removal in AnimateRelicAwards, found {replaced}.", 0);
            }
        }
    }

    [HarmonyPatch(typeof(NTreasureRoomRelicCollection), "RefreshNavigation")]
    internal static class RefreshNavigation_Patch
    {
        private static readonly AccessTools.FieldRef<NTreasureRoomRelicCollection, List<NTreasureRoomRelicHolder>> HoldersRef =
            AccessTools.FieldRefAccess<NTreasureRoomRelicCollection, List<NTreasureRoomRelicHolder>>("_holdersInUse");

        internal static void Prefix(NTreasureRoomRelicCollection __instance)
        {
            try { HolderRemoval.Apply(HoldersRef(__instance)); }
            catch (Exception ex) { Log.Error($"[RelicShare] Holder cleanup failed: {ex}", 0); }
        }
    }
}
