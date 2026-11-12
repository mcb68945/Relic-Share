using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Godot;
using MegaCrit.Sts2.Core.Logging;

namespace RelicShare
{
    /// <summary>
    /// Reflection-only bridge to the third-party ModConfig framework (https://github.com/xhyrzldf/ModConfig-STS2).
    /// There is no compile-time dependency on ModConfig - if it isn't installed, Detect() finds nothing and the
    /// mod just stays always-on, same as before this existed.
    /// </summary>
    internal static class ModConfigBridge
    {
        private const string ModId = "RelicShare";

        private static bool _available;
        private static bool _registered;
        private static Type? _apiType;
        private static Type? _entryType;
        private static Type? _configTypeEnum;

        internal static void DeferredRegister()
        {
            var tree = (SceneTree)Engine.GetMainLoop();
            tree.ProcessFrame += OnNextFrame;
        }

        private static void OnNextFrame()
        {
            var tree = (SceneTree)Engine.GetMainLoop();
            tree.ProcessFrame -= OnNextFrame;
            Detect();
            if (_available)
            {
                Register();
            }
        }

        private static void Detect()
        {
            try
            {
                Type[] allTypes = AppDomain.CurrentDomain.GetAssemblies()
                    .SelectMany(a =>
                    {
                        try { return a.GetTypes(); }
                        catch { return Type.EmptyTypes; }
                    })
                    .ToArray();

                _apiType = allTypes.FirstOrDefault(t => t.FullName == "ModConfig.ModConfigApi");
                _entryType = allTypes.FirstOrDefault(t => t.FullName == "ModConfig.ConfigEntry");
                _configTypeEnum = allTypes.FirstOrDefault(t => t.FullName == "ModConfig.ConfigType");
                _available = _apiType != null && _entryType != null && _configTypeEnum != null;
            }
            catch
            {
                _available = false;
            }
        }

        private static void Register()
        {
            if (_registered) { return; }
            _registered = true;
            try
            {
                Array entries = BuildEntries();
                var displayNames = new Dictionary<string, string> { ["en"] = "Relic Share" };

                MethodInfo registerMethod = _apiType!.GetMethods(BindingFlags.Static | BindingFlags.Public)
                    .Where(m => m.Name == "Register")
                    .OrderByDescending(m => m.GetParameters().Length)
                    .First();

                if (registerMethod.GetParameters().Length == 4)
                {
                    registerMethod.Invoke(null, new object[] { ModId, displayNames["en"], displayNames, entries });
                }
                else
                {
                    registerMethod.Invoke(null, new object[] { ModId, displayNames["en"], entries });
                }

                RelicShareMod.ApplyEnabled(GetValue("modEnabled", true));
            }
            catch (Exception ex)
            {
                Log.Warn($"[RelicShare] ModConfig registration failed, Relic Share remains always-on: {ex}", 0);
            }
        }

        internal static T GetValue<T>(string key, T fallback)
        {
            if (!_available) { return fallback; }
            try
            {
                object? result = _apiType!.GetMethod("GetValue", BindingFlags.Static | BindingFlags.Public)
                    ?.MakeGenericMethod(typeof(T))
                    ?.Invoke(null, new object[] { ModId, key });
                return result is T value ? value : fallback;
            }
            catch
            {
                return fallback;
            }
        }

        private static Array BuildEntries()
        {
            var list = new List<object>
            {
                Entry(cfg =>
                {
                    Set(cfg, "Key", "modEnabled");
                    Set(cfg, "Label", "Enable Relic Share");
                    Set(cfg, "Type", EnumVal("Toggle"));
                    Set(cfg, "DefaultValue", true);
                    Set(cfg, "Description",
                        "Adds an extra relic choice per chest in multiplayer, and lets tied players re-pick instead " +
                        "of getting a random leftover relic. Turn this off to play a run with someone who doesn't " +
                        "have Relic Share installed, without deleting any files.");
                    Set(cfg, "OnChanged", (Action<object>)(v => RelicShareMod.ApplyEnabled(Convert.ToBoolean(v))));
                })
            };

            Array array = Array.CreateInstance(_entryType!, list.Count);
            for (int i = 0; i < list.Count; i++)
            {
                array.SetValue(list[i], i);
            }
            return array;
        }

        private static object Entry(Action<object> configure)
        {
            object obj = Activator.CreateInstance(_entryType!)!;
            configure(obj);
            return obj;
        }

        private static void Set(object obj, string name, object value)
        {
            obj.GetType().GetProperty(name)?.SetValue(obj, value);
        }

        private static object EnumVal(string name)
        {
            return Enum.Parse(_configTypeEnum!, name);
        }
    }
}
