using System;
using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;

namespace RelicShare
{
    [ModInitializer(nameof(ModLoaded))]
    public static class RelicShareMod
    {
        internal const string HarmonyId = "RelicShare";
        internal static Harmony? HarmonyInstance;
        private static bool _patched;

        public static void ModLoaded()
        {
            HarmonyInstance = new Harmony(HarmonyId);
            ApplyEnabled(true);
            ModConfigBridge.DeferredRegister();
        }

        internal static void ApplyEnabled(bool enabled)
        {
            if (HarmonyInstance == null) { return; }
            if (enabled && !_patched)
            {
                try
                {
                    HarmonyInstance.PatchAll(Assembly.GetExecutingAssembly());
                    _patched = true;
                    Log.Info("[RelicShare] Mod enabled - patches applied.", 0);
                }
                catch (Exception ex)
                {
                    // PatchAll isn't transactional - if a game update renamed/restructured something this mod
                    // reflects into, it can throw partway through, leaving some of our patches applied and
                    // others not. A half-applied mod is worse than a mod that didn't load at all, so roll
                    // everything back to a clean, fully-unpatched state rather than leaving that up to chance.
                    Log.Error($"[RelicShare] Failed to apply patches (the game may have changed since this mod was built) - rolling back to fully unpatched: {ex}", 0);
                    try { HarmonyInstance.UnpatchAll(HarmonyId); }
                    catch (Exception unpatchEx) { Log.Error($"[RelicShare] Rollback after a failed patch also failed: {unpatchEx}", 0); }
                    _patched = false;
                }
            }
            else if (!enabled && _patched)
            {
                try
                {
                    HarmonyInstance.UnpatchAll(HarmonyId);
                    Log.Info("[RelicShare] Mod disabled from Mod Settings - patches removed, native relic-picking behavior restored.", 0);
                }
                catch (Exception ex)
                {
                    Log.Error($"[RelicShare] Failed to cleanly unpatch: {ex}", 0);
                }
                finally
                {
                    _patched = false;
                }
            }
        }
    }
}
