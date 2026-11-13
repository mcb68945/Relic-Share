using System;
using System.Collections.Generic;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes.Screens.TreasureRoomRelic;

namespace RelicShare
{
    /// <summary>
    /// In co-op the treasure screen hides the mouse and draws each player's hand as their pointer. When relics are
    /// awarded the game freezes every hand (parks it while the fight/grab plays) and never unfreezes it, because
    /// natively nobody picks again after a fight. A tie re-pick does pick again, so without this the re-picking
    /// player has a frozen hand and no visible mouse. Call after a re-pick round's animation, before picking resumes.
    /// </summary>
    internal static class Hands
    {
        private static readonly AccessTools.FieldRef<NTreasureRoomRelicCollection, NHandImageCollection> CollectionRef =
            AccessTools.FieldRefAccess<NTreasureRoomRelicCollection, NHandImageCollection>("_hands");
        private static readonly AccessTools.FieldRef<NHandImageCollection, List<NHandImage>> HandsRef =
            AccessTools.FieldRefAccess<NHandImageCollection, List<NHandImage>>("_hands");

        /// <summary>Unfreeze the hand of every player for whom <paramref name="stillPicking"/> is true.</summary>
        internal static void Unfreeze(NTreasureRoomRelicCollection? screen, Func<ulong, bool> stillPicking)
        {
            if (screen == null) { return; }
            try
            {
                NHandImageCollection? collection = CollectionRef(screen);
                if (collection == null) { return; }
                foreach (NHandImage hand in HandsRef(collection))
                {
                    if (hand?.Player == null || !stillPicking(hand.Player.NetId)) { continue; }
                    hand.SetIsInFight(false);
                    hand.SetFrozenForRelicAwards(false);
                }
            }
            catch (Exception ex)
            {
                Log.Error($"[RelicShare] Couldn't give the re-picking player their hand back: {ex}", 0);
            }
        }
    }
}
