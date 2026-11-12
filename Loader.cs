using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using HarmonyLib;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;

namespace RelicShare
{
    /// <summary>
    /// The game loads only RelicShare.dll (the DLL named after the mod id). This loader checks which shape of the
    /// game's chest code is running and loads the matching build from the same folder:
    ///   RelicShare.V113.dll - game v0.113+ (chests can have several picking rounds)
    ///   RelicShare.V111.dll - game v0.111 (one picking round per chest)
    /// One download therefore works on both the live and beta branches. Detection is by feature, not version number,
    /// so a later patch keeps working as long as the chest code keeps the same shape.
    /// </summary>
    [ModInitializer(nameof(ModLoaded))]
    public static class Loader
    {
        private const string ModId = "RelicShare";

        public static void ModLoaded()
        {
            try
            {
                Type? sync = AccessTools.TypeByName("MegaCrit.Sts2.Core.Multiplayer.Game.TreasureRoomRelicSynchronizer");
                bool multiRound = sync != null && AccessTools.Field(sync, "_roundsLeft") != null;
                string build = multiRound ? "RelicShare.V113" : "RelicShare.V111";

                Assembly self = typeof(Loader).Assembly;
                string? dir = Path.GetDirectoryName(self.Location);
                if (string.IsNullOrEmpty(dir))
                {
                    dir = ModManager.GetLoadedMods().FirstOrDefault(m => m?.manifest?.id == ModId)?.path;
                }
                if (string.IsNullOrEmpty(dir))
                {
                    Log.Error($"[{ModId}] Couldn't find the mod's folder - mod not loaded.", 0);
                    return;
                }

                string path = Path.Combine(dir, build + ".dll");
                if (!File.Exists(path))
                {
                    Log.Error($"[{ModId}] {build}.dll is missing from {dir} - reinstall the mod. Mod not loaded.", 0);
                    return;
                }

                AssemblyLoadContext context = AssemblyLoadContext.GetLoadContext(self) ?? AssemblyLoadContext.Default;
                Assembly inner = context.LoadFromAssemblyPath(path);
                Log.Info($"[{ModId}] Game chest code is {(multiRound ? "multi-round (v0.113+)" : "single-round (v0.111)")} - loading {build}.", 0);

                MethodInfo? init = inner.GetType("RelicShare.RelicShareMod")?.GetMethod("ModLoaded", BindingFlags.Public | BindingFlags.Static);
                if (init == null)
                {
                    Log.Error($"[{ModId}] {build}.dll has no RelicShareMod.ModLoaded - mod not loaded.", 0);
                    return;
                }
                init.Invoke(null, null);
            }
            catch (Exception ex)
            {
                Log.Error($"[{ModId}] Loader failed - mod not loaded: {ex}", 0);
            }
        }
    }
}
