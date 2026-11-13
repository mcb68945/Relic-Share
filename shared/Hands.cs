using System;
using System.Collections.Generic;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Screens.TreasureRoomRelic;

namespace RelicShare
{
    /// <summary>
    /// In co-op the treasure screen hides the mouse and draws each player's hand as their pointer. The award
    /// animation freezes every hand, swaps losers to a rock/paper/scissors texture and fades them, and parks the
    /// winner's arm (holding the relic) mid-screen. Natively nobody picks again after that, so none of it is undone.
    /// For a tie re-pick: re-pickers get a working pointing hand back plus the normal mouse cursor (belt and braces),
    /// and players who are done have their arm moved off screen so it doesn't cover the chest.
    /// </summary>
    internal static class Hands
    {
        private static readonly AccessTools.FieldRef<NTreasureRoomRelicCollection, NHandImageCollection> CollectionRef =
            AccessTools.FieldRefAccess<NTreasureRoomRelicCollection, NHandImageCollection>("_hands");
        private static readonly AccessTools.FieldRef<NHandImageCollection, List<NHandImage>> HandsRef =
            AccessTools.FieldRefAccess<NHandImageCollection, List<NHandImage>>("_hands");
        private static readonly AccessTools.FieldRef<NHandImage, TextureRect> TextureRef =
            AccessTools.FieldRefAccess<NHandImage, TextureRect>("_textureRect");

        /// <summary>True while the local player has to pick again in a tie re-pick. Set by each game-version build.</summary>
        internal static Func<bool> LocalRepicking = () => false;

        internal static void ResetForRepick(NTreasureRoomRelicCollection? screen, Func<ulong, bool> stillPicking)
        {
            if (screen == null) { return; }
            try
            {
                NHandImageCollection? collection = CollectionRef(screen);
                if (collection == null) { return; }
                int fixedHands = 0;
                foreach (NHandImage hand in HandsRef(collection))
                {
                    if (hand?.Player == null || !GodotObject.IsInstanceValid(hand)) { continue; }
                    bool isMe = LocalContext.IsMe(hand.Player);
                    if (!stillPicking(hand.Player.NetId))
                    {
                        hand.AnimateAway(); // done with this chest: get the arm out of the way
                        continue;
                    }
                    hand.SetIsInFight(false);
                    hand.SetFrozenForRelicAwards(false);
                    TextureRect tex = TextureRef(hand);
                    if (tex != null)
                    {
                        tex.Texture = hand.Player.Character.ArmPointingTexture;
                        tex.Rotation = 0f;
                    }
                    hand.Modulate = isMe ? Colors.White : new Color(0.5f, 0.5f, 0.5f, 0.5f);
                    if (isMe) { hand.SetPointingPosition(screen.GetGlobalMousePosition()); }
                    fixedHands++;
                }
                ShowCursorIfRepicking();
                Log.Info($"[RelicShare] Re-pick: reset {fixedHands} hand(s); local player picking again: {LocalRepicking()}.", 0);
            }
            catch (Exception ex)
            {
                Log.Error($"[RelicShare] Couldn't reset hands for the re-pick: {ex}", 0);
            }
        }

        internal static void ShowCursorIfRepicking()
        {
            try
            {
                if (LocalRepicking()) { NGame.Instance?.CursorManager?.SetCursorShown(show: true); }
            }
            catch (Exception ex) { Log.Error($"[RelicShare] Couldn't show the cursor: {ex}", 0); }
        }
    }

    /// <summary>The game re-hides the cursor whenever hands refresh; keep it visible for a local re-picker.</summary>
    [HarmonyPatch(typeof(NHandImageCollection), "UpdateHandVisibility")]
    internal static class UpdateHandVisibility_Patch
    {
        internal static void Postfix() => Hands.ShowCursorIfRepicking();
    }
}
