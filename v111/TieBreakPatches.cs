using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using HarmonyLib;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.TreasureRelicPicking;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Nodes.Screens.TreasureRoomRelic;
using MegaCrit.Sts2.Core.Random;
using MegaCrit.Sts2.Core.Runs;

namespace RelicShare
{
    /// <summary>
    /// Core tie-break logic. When a relic is tied, instead of the native "instant RNG fight, consolation prize
    /// for everyone else" behavior, this resolves genuine fights (GenerateRelicFight) but makes everyone who
    /// didn't get a relic this wave ("needy" players) vote again on what's left, recursing wave by wave until
    /// nobody is left empty-handed who still has something to pick from.
    ///
    /// Round 9 change: each wave's result(s) are now handed to the native RelicsAwarded event (and therefore the
    /// native fight animation) IMMEDIATELY as that wave resolves, instead of being batched up and only fired once
    /// the whole multi-wave round finishes. Previously, a genuine tie's fight animation didn't play until *after*
    /// the loser's second pick, because RelicsAwarded only ever fired once, right at the very end, with every
    /// wave's results bundled together - see NTreasureRoomRelicCollection_SuppressCompletionSignals_Patch below
    /// for why that requires suppressing two of the native screen's "I'm done" signals on every wave but the last.
    /// </summary>
    [HarmonyPatch(typeof(TreasureRoomRelicSynchronizer), "AwardRelics")]
    internal static class TreasureRoomRelicSynchronizer_AwardRelics_Patch
    {
        private sealed class PendingRound
        {
            public required List<RelicPickingResult> Results;
            public required List<Player> NeedyPlayers;
            public required List<RelicModel> OriginalRelics;
            public required HashSet<int> ClaimedIndices;
        }

        private static readonly FieldInfo PlayerCollectionField = AccessTools.Field(typeof(TreasureRoomRelicSynchronizer), "_playerCollection");
        private static readonly FieldInfo VotesField = AccessTools.Field(typeof(TreasureRoomRelicSynchronizer), "_votes");
        private static readonly FieldInfo CurrentRelicsField = AccessTools.Field(typeof(TreasureRoomRelicSynchronizer), "_currentRelics");
        private static readonly FieldInfo RngField = AccessTools.Field(typeof(TreasureRoomRelicSynchronizer), "_rng");
        private static readonly FieldInfo RelicsAwardedField = AccessTools.Field(typeof(TreasureRoomRelicSynchronizer), "RelicsAwarded");
        private static readonly FieldInfo VotesChangedField = AccessTools.Field(typeof(TreasureRoomRelicSynchronizer), "VotesChanged");
        private static readonly MethodInfo EndRelicVotingMethod = AccessTools.Method(typeof(TreasureRoomRelicSynchronizer), "EndRelicVoting");

        private static readonly ConditionalWeakTable<TreasureRoomRelicSynchronizer, PendingRound> Pending = new();

        internal static bool HasPendingRound(TreasureRoomRelicSynchronizer instance) => Pending.TryGetValue(instance, out _);

        internal static bool TryGetOriginalRelics(TreasureRoomRelicSynchronizer instance, out List<RelicModel> originalRelics)
        {
            if (Pending.TryGetValue(instance, out var pending))
            {
                originalRelics = pending.OriginalRelics;
                return true;
            }
            originalRelics = null!;
            return false;
        }

        internal static bool TryGetClaimedIndices(TreasureRoomRelicSynchronizer instance, out HashSet<int> claimedIndices)
        {
            if (Pending.TryGetValue(instance, out var pending))
            {
                claimedIndices = pending.ClaimedIndices;
                return true;
            }
            claimedIndices = null!;
            return false;
        }

        internal static bool TryGetNeedyPlayers(TreasureRoomRelicSynchronizer instance, out List<Player> needyPlayers)
        {
            if (Pending.TryGetValue(instance, out var pending))
            {
                needyPlayers = pending.NeedyPlayers;
                return true;
            }
            needyPlayers = null!;
            return false;
        }

        internal static bool Prefix(TreasureRoomRelicSynchronizer __instance)
        {
            try
            {
                var playerCollection = (IPlayerCollection)PlayerCollectionField.GetValue(__instance)!;
                var votes = (List<TreasureRoomRelicSynchronizer.PlayerVote>)VotesField.GetValue(__instance)!;

                if (Pending.TryGetValue(__instance, out var pending))
                {
                    ResolveRound(__instance, playerCollection, votes, pending.NeedyPlayers, pending.OriginalRelics, pending.Results, pending.ClaimedIndices, invokeEndRelicVotingIfFinished: false);
                    return false;
                }

                var currentRelics = (List<RelicModel>)CurrentRelicsField.GetValue(__instance)!;
                var players = playerCollection.Players;
                if (!NeedsATieBreakChoice(playerCollection, votes, players, currentRelics))
                {
                    return true;
                }

                ResolveRound(__instance, playerCollection, votes, players, currentRelics, new List<RelicPickingResult>(), new HashSet<int>(), invokeEndRelicVotingIfFinished: false);
                return false;
            }
            catch (Exception ex)
            {
                Log.Error($"[RelicShare] Tie re-pick patch failed, falling back to native relic awarding: {ex}", 0);
                Pending.Remove(__instance);
                return true;
            }
        }

        private static bool NeedsATieBreakChoice(IPlayerCollection playerCollection, List<TreasureRoomRelicSynchronizer.PlayerVote> votes, IReadOnlyList<Player> players, List<RelicModel> currentRelics)
        {
            var votedIndices = new HashSet<int>();
            foreach (var player in players)
            {
                var vote = votes[playerCollection.GetPlayerSlotIndex(player)];
                if (vote.index.HasValue)
                {
                    votedIndices.Add(vote.index.Value);
                }
            }

            int leftoverRelics = currentRelics.Count - votedIndices.Count;
            int stillNeedyPlayers = players.Count - votedIndices.Count;
            return leftoverRelics > 1 && stillNeedyPlayers > 0;
        }

        private static void ResolveRound(
            TreasureRoomRelicSynchronizer instance,
            IPlayerCollection playerCollection,
            List<TreasureRoomRelicSynchronizer.PlayerVote> votes,
            IReadOnlyList<Player> players,
            List<RelicModel> relics,
            List<RelicPickingResult> priorResults,
            HashSet<int> priorClaimedIndices,
            bool invokeEndRelicVotingIfFinished)
        {
            var rng = (Rng)RngField.GetValue(instance)!;

            var votersByIndex = new Dictionary<int, List<Player>>();
            foreach (var player in players)
            {
                var vote = votes[playerCollection.GetPlayerSlotIndex(player)];
                if (!vote.index.HasValue) { continue; }
                if (!votersByIndex.TryGetValue(vote.index.Value, out var voters))
                {
                    voters = new List<Player>();
                    votersByIndex[vote.index.Value] = voters;
                }
                voters.Add(player);
            }

            var results = new List<RelicPickingResult>(priorResults);
            var claimedIndices = new HashSet<int>(priorClaimedIndices);
            var newlyResolved = new List<RelicPickingResult>();

            foreach (var (index, voters) in votersByIndex)
            {
                if (claimedIndices.Contains(index)) { continue; }

                var relic = relics[index];
                var result = voters.Count != 1
                    ? RelicPickingResult.GenerateRelicFight(voters, relic, () => rng.NextItem(Enum.GetValues<RelicPickingFightMove>()))
                    : new RelicPickingResult { type = RelicPickingResultType.OnlyOnePlayerVoted, relic = relic, player = voters[0] };

                results.Add(result);
                newlyResolved.Add(result);
                claimedIndices.Add(index);
            }

            var needyPlayers = players.Where(p => !results.Any(r => r.player == p)).ToList();
            bool finished = needyPlayers.Count == 0;

            // Fire RelicsAwarded for just this wave's new result(s), right away - not batched with any later
            // wave - so a genuine fight animation plays immediately after the tie instead of being deferred
            // until the whole multi-wave round finishes.
            if (newlyResolved.Count > 0 && RelicsAwardedField.GetValue(instance) is Action<List<RelicPickingResult>> relicsAwarded)
            {
                if (finished)
                {
                    NTreasureRoomRelicCollection_SuppressCompletionSignals_Patch.SuppressPickingComplete = false;
                    relicsAwarded(newlyResolved);
                }
                else
                {
                    // Not the last wave - suppress the native "picking begun/finished" signals so the chest
                    // doesn't think it's done and start closing after the first fight. We get told (via the
                    // callback below) exactly when this wave's animation has actually finished playing, and
                    // only then do we reset the needy player(s)' votes and let them pick again.
                    Pending.AddOrUpdate(instance, new PendingRound
                    {
                        Results = results,
                        NeedyPlayers = needyPlayers,
                        OriginalRelics = relics,
                        ClaimedIndices = claimedIndices
                    });

                    NTreasureRoomRelicCollection_SuppressCompletionSignals_Patch.SuppressPickingComplete = true;
                    NTreasureRoomRelicCollection_SuppressCompletionSignals_Patch.OnSuppressedPickingComplete = () =>
                    {
                        NTreasureRoomRelicCollection_SuppressCompletionSignals_Patch.OnSuppressedPickingComplete = null;
                        UnlockNeedyPlayers(instance, playerCollection, votes, needyPlayers, relics, claimedIndices);
                    };
                    relicsAwarded(newlyResolved);
                    return;
                }
            }

            if (finished)
            {
                Pending.Remove(instance);
                Log.Info($"[RelicShare] Tie re-pick finished - {results.Count - priorResults.Count} pick(s) resolved this round, {results.Count} relic(s) awarded in total.", 0);
                if (invokeEndRelicVotingIfFinished)
                {
                    EndRelicVotingMethod.Invoke(instance, null);
                }
                return;
            }

            // No animation to wait for (shouldn't normally happen, but don't get stuck if it does) - unlock now.
            Pending.AddOrUpdate(instance, new PendingRound
            {
                Results = results,
                NeedyPlayers = needyPlayers,
                OriginalRelics = relics,
                ClaimedIndices = claimedIndices
            });
            UnlockNeedyPlayers(instance, playerCollection, votes, needyPlayers, relics, claimedIndices);
        }

        private static void UnlockNeedyPlayers(
            TreasureRoomRelicSynchronizer instance,
            IPlayerCollection playerCollection,
            List<TreasureRoomRelicSynchronizer.PlayerVote> votes,
            List<Player> needyPlayers,
            List<RelicModel> relics,
            HashSet<int> claimedIndices)
        {
            foreach (var player in needyPlayers)
            {
                var vote = votes[playerCollection.GetPlayerSlotIndex(player)];
                vote.index = null;
                vote.voteReceived = false;
            }

            Log.Info($"[RelicShare] Tie on a relic - {needyPlayers.Count} player(s) choosing between {relics.Count - claimedIndices.Count} leftover relic(s) instead of getting one auto-assigned.", 0);

            NTreasureRoomRelicCollection_SuppressCompletionSignals_Patch.Current?.SetSelectionEnabled(true);

            // The award animation froze every hand (= the co-op mouse pointer); give the re-pickers theirs back.
            var needyIds = new HashSet<ulong>(needyPlayers.Select(p => p.NetId));
            Hands.Unfreeze(NTreasureRoomRelicCollection_SuppressCompletionSignals_Patch.Current, needyIds.Contains);

            if (VotesChangedField.GetValue(instance) is Action votesChanged)
            {
                votesChanged();
            }
        }

        internal static void TryCompleteRoundIfReady(TreasureRoomRelicSynchronizer instance)
        {
            try
            {
                if (Pending.TryGetValue(instance, out var pending))
                {
                    var playerCollection = (IPlayerCollection)PlayerCollectionField.GetValue(instance)!;
                    var votes = (List<TreasureRoomRelicSynchronizer.PlayerVote>)VotesField.GetValue(instance)!;
                    if (pending.NeedyPlayers.All(p => votes[playerCollection.GetPlayerSlotIndex(p)].voteReceived))
                    {
                        Log.Warn($"[RelicShare] Resolving a tie re-pick manually - native \"has everyone voted\" never re-fired on its own even though all {pending.NeedyPlayers.Count} needy player(s) had voted.", 0);
                        ResolveRound(instance, playerCollection, votes, pending.NeedyPlayers, pending.OriginalRelics, pending.Results, pending.ClaimedIndices, invokeEndRelicVotingIfFinished: true);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error($"[RelicShare] Manual tie re-pick completion check failed: {ex}", 0);
            }
        }
    }

    [HarmonyPatch(typeof(TreasureRoomRelicSynchronizer), "OnPicked")]
    internal static class TreasureRoomRelicSynchronizer_OnPicked_TryComplete_Patch
    {
        internal static void Postfix(TreasureRoomRelicSynchronizer __instance)
        {
            TreasureRoomRelicSynchronizer_AwardRelics_Patch.TryCompleteRoundIfReady(__instance);
        }
    }

    [HarmonyPatch(typeof(TreasureRoomRelicSynchronizer), "EndRelicVoting")]
    internal static class TreasureRoomRelicSynchronizer_EndRelicVoting_Patch
    {
        internal static bool Prefix(TreasureRoomRelicSynchronizer __instance)
        {
            try
            {
                return !TreasureRoomRelicSynchronizer_AwardRelics_Patch.HasPendingRound(__instance);
            }
            catch (Exception ex)
            {
                Log.Error($"[RelicShare] Pending-round check before EndRelicVoting failed, letting native behavior proceed: {ex}", 0);
                return true;
            }
        }
    }

    [HarmonyPatch]
    internal static class TreasureRoomRelicSynchronizer_RestoreCurrentRelics_Patches
    {
        private static readonly FieldInfo CurrentRelicsField = AccessTools.Field(typeof(TreasureRoomRelicSynchronizer), "_currentRelics");

        internal static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(TreasureRoomRelicSynchronizer), "PickRelicLocally");
            yield return AccessTools.Method(typeof(TreasureRoomRelicSynchronizer), "OnPicked");
        }

        internal static void Prefix(TreasureRoomRelicSynchronizer __instance)
        {
            try
            {
                if (CurrentRelicsField.GetValue(__instance) == null &&
                    TreasureRoomRelicSynchronizer_AwardRelics_Patch.TryGetOriginalRelics(__instance, out var originalRelics))
                {
                    Log.Warn($"[RelicShare] _currentRelics was unexpectedly null with a tie re-pick still pending - restoring the original {originalRelics.Count}-relic list so picking still works.", 0);
                    CurrentRelicsField.SetValue(__instance, originalRelics);
                }
            }
            catch (Exception ex)
            {
                Log.Error($"[RelicShare] _currentRelics restore check failed, leaving native behavior alone: {ex}", 0);
            }
        }
    }

    [HarmonyPatch(typeof(TreasureRoomRelicSynchronizer), "PickRelicLocally")]
    internal static class TreasureRoomRelicSynchronizer_BlockResolvedPlayerPicks_Patch
    {
        private static readonly PropertyInfo LocalPlayerProperty = AccessTools.Property(typeof(TreasureRoomRelicSynchronizer), "LocalPlayer");

        internal static bool Prefix(TreasureRoomRelicSynchronizer __instance)
        {
            try
            {
                if (!TreasureRoomRelicSynchronizer_AwardRelics_Patch.TryGetNeedyPlayers(__instance, out var needyPlayers))
                {
                    return true; // no tie re-pick in progress for this room - nothing to block
                }

                var localPlayer = (Player?)LocalPlayerProperty.GetValue(__instance);
                if (localPlayer != null && !needyPlayers.Contains(localPlayer))
                {
                    Log.Info(
                        "[RelicShare] Ignored a relic click - this player already has a relic from this chest, only the still-needy player(s) get to pick again.",
                        0);
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                Log.Error($"[RelicShare] Resolved-player click check failed, letting the click through: {ex}", 0);
                return true;
            }
        }
    }

    [HarmonyPatch(typeof(NTreasureRoomRelicCollection), "RefreshVotes")]
    internal static class NTreasureRoomRelicCollection_RefreshVotes_Patch
    {
        private static readonly FieldInfo HoldersInUseField = AccessTools.Field(typeof(NTreasureRoomRelicCollection), "_holdersInUse");

        internal static void Postfix(NTreasureRoomRelicCollection __instance)
        {
            try
            {
                var synchronizer = RunManager.Instance?.TreasureRoomRelicSynchronizer;
                if (synchronizer == null ||
                    !TreasureRoomRelicSynchronizer_AwardRelics_Patch.TryGetClaimedIndices(synchronizer, out var claimedIndices) ||
                    HoldersInUseField.GetValue(__instance) is not IEnumerable holders)
                {
                    return;
                }

                foreach (var item in holders)
                {
                    if (item is NTreasureRoomRelicHolder holder && claimedIndices.Contains(holder.Index))
                    {
                        holder.Visible = false;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error($"[RelicShare] Failed to hide already-claimed relic(s) during a tie re-pick: {ex}", 0);
            }
        }
    }

    /// <summary>
    /// Round 9. NTreasureRoomRelicCollection.AnimateRelicAwards() - the coroutine the native "fight" animation
    /// lives in - is only ever meant to run once per chest: at its start it signals "picking has begun" and at
    /// its end it signals "picking is finished" (which is what tells the room to close the chest screen), each
    /// via a TaskCompletionSource that can only have SetResult() called on it once, ever.
    ///
    /// Our mod now calls into that coroutine once per wave (so a genuine fight's animation plays immediately,
    /// instead of being deferred to the end of the whole multi-wave round - see TieBreakPatches.cs). Left alone,
    /// that would throw on the second call ("already completed") and - worse - the "picking is finished" signal
    /// firing after just the FIRST wave would make the room think the chest is done and start closing itself
    /// before the loser ever gets to re-pick.
    ///
    /// This patch transpiles that coroutine's two SetResult() calls into a guarded version: the "begun" signal
    /// is simply allowed to fire only the first time it's hit (harmless either way - it only gates a
    /// single-player "skip" button), and the "finished" signal is suppressed on every wave but the real last
    /// one, with a callback fired instead so our mod knows exactly when it's safe to let the needy player pick
    /// again.
    /// </summary>
    [HarmonyPatch]
    internal static class NTreasureRoomRelicCollection_SuppressCompletionSignals_Patch
    {
        internal static NTreasureRoomRelicCollection? Current;
        internal static bool SuppressPickingComplete;
        internal static Action? OnSuppressedPickingComplete;

        private static bool _beganAlreadySignaled;

        [HarmonyPatch(typeof(NTreasureRoomRelicCollection), "InitializeRelics")]
        private static class CaptureInstance
        {
            internal static void Postfix(NTreasureRoomRelicCollection __instance)
            {
                Current = __instance;
                _beganAlreadySignaled = false;
                SuppressPickingComplete = false;
                OnSuppressedPickingComplete = null;
            }
        }

        internal static void MaybeSetBegan(TaskCompletionSource tcs)
        {
            if (_beganAlreadySignaled) { return; }
            _beganAlreadySignaled = true;
            tcs.SetResult();
        }

        internal static void MaybeSetComplete(TaskCompletionSource tcs)
        {
            if (SuppressPickingComplete)
            {
                var callback = OnSuppressedPickingComplete;
                callback?.Invoke();
            }
            else
            {
                tcs.SetResult();
            }
        }

        private static MethodBase TargetMethod()
        {
            var stateMachine = typeof(NTreasureRoomRelicCollection)
                .GetNestedTypes(BindingFlags.NonPublic | BindingFlags.Public)
                .First(t => t.Name.Contains("AnimateRelicAwards"));
            return AccessTools.Method(stateMachine, "MoveNext");
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var setResult = AccessTools.Method(typeof(TaskCompletionSource), "SetResult", Type.EmptyTypes);
            var maybeBegan = AccessTools.Method(typeof(NTreasureRoomRelicCollection_SuppressCompletionSignals_Patch), nameof(MaybeSetBegan));
            var maybeComplete = AccessTools.Method(typeof(NTreasureRoomRelicCollection_SuppressCompletionSignals_Patch), nameof(MaybeSetComplete));

            var list = instructions.ToList();
            for (int i = 0; i < list.Count; i++)
            {
                var instruction = list[i];
                if (instruction.Calls(setResult))
                {
                    FieldInfo? targetField = null;
                    for (int j = i - 1; j >= 0 && j >= i - 6; j--)
                    {
                        if (list[j].operand is FieldInfo field)
                        {
                            targetField = field;
                            break;
                        }
                    }

                    bool isComplete = targetField?.Name.Contains("Complete", StringComparison.OrdinalIgnoreCase) == true;
                    yield return new CodeInstruction(OpCodes.Call, isComplete ? maybeComplete : maybeBegan);
                }
                else
                {
                    yield return instruction;
                }
            }
        }
    }
}
