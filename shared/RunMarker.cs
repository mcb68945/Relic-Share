using System;
using System.Collections.Generic;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;

namespace RelicShare
{
    /// <summary>
    /// A run where Relic +1 put an extra relic in a chest is saved to run history as a custom run. Community stats
    /// sites (Spire Codex and others) leave custom runs out, so buffed runs don't skew their relic numbers.
    /// v0.113+ has an is_custom_game flag; v0.111 marks custom runs with GameMode.Custom.
    /// </summary>
    internal static class RunMarker
    {
        private static readonly HashSet<string> BoostedSeeds = new();

        internal static void Boosted(IRunState? runState)
        {
            try { if (runState?.Rng?.StringSeed is { Length: > 0 } seed) BoostedSeeds.Add(seed); }
            catch { /* marking is best effort */ }
        }

        [HarmonyPatch(typeof(SaveManager), nameof(SaveManager.SaveRunHistory))]
        internal static class SaveRunHistory_Patch
        {
            internal static void Prefix(RunHistory history)
            {
                try
                {
                    if (history == null || !BoostedSeeds.Remove(history.Seed)) { return; }
                    var t = history.GetType();
                    var custom = t.GetProperty("IsCustomGame");
                    if (custom != null && custom.CanWrite) { custom.SetValue(history, true); }
                    else if (t.GetProperty("GameMode") is { CanWrite: true } gm && Enum.TryParse(gm.PropertyType, "Custom", out var v)) { gm.SetValue(history, v); }
                    Log.Info("[RelicShare] Run used Relic +1 chests; saved to history as a custom run.", 0);
                }
                catch (Exception ex) { Log.Error($"[RelicShare] Couldn't mark run history: {ex.Message}", 0); }
            }
        }
    }
}
