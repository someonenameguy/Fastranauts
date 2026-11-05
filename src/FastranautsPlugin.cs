using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using Fastranauts.Common;
using Fastranauts.Modules;
using HarmonyLib;
using UnityEngine;

namespace Fastranauts
{
    /// <summary>
    /// Main BepInEx entry point for Fastranauts Performance Optimizer.
    /// Designed with strict fault isolation and non-destructive Harmony Transpilers/Hooks
    /// so it never interferes with other Ostranauts mods (such as Take Their Ships or JSON content mods).
    /// </summary>
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class FastranautsPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "com.fastranauts.performance";
        public const string PluginName = "Fastranauts Performance Optimizer";
        public const string PluginVersion = "1.1.0";

        public static FastranautsPlugin Instance { get; private set; }

        private Harmony _harmony;
        private readonly List<IFastModule> _modules = new List<IFastModule>();
        private ConfigEntry<bool> _masterEnableConfig;
        private ConfigEntry<KeyCode> _telemetryKeyConfig;

        private void Awake()
        {
            Instance = this;

            _masterEnableConfig = Config.Bind(
                "00. General",
                "MasterEnable",
                true,
                "Master switch for Fastranauts Performance Optimizer. Set to false to disable all patches.");

            _telemetryKeyConfig = Config.Bind(
                "00. General",
                "LogTelemetryHotkey",
                KeyCode.F8,
                "Press this key in-game (or type 'fastinfo' in the in-game debug console) to log live Fastranauts optimization telemetry.");

            if (!_masterEnableConfig.Value)
            {
                Logger.LogInfo("[Fastranauts] MasterEnable is false in config. Skipping all patches.");
                return;
            }

            _harmony = new Harmony(PluginGuid);

            _modules.Add(new PdaOverlayOptimizationModule());
            _modules.Add(new SensorAndDoorOptimizationModule());
            _modules.Add(new RaycastAndGcOptimizationModule());
            _modules.Add(new VisibilityLightOptimizationModule());
            _modules.Add(new UiAndTooltipOptimizationModule());
            _modules.Add(new PathfindingAndAiOptimizationModule());
            _modules.Add(new PauseAndTimeScaleOptimizationModule());
            _modules.Add(new UpdateDecouplingAndSimTickModule());

            int activeCount = 0;
            foreach (IFastModule module in _modules)
            {
                try
                {
                    module.Initialize(Config, Logger, _harmony);
                    if (module.IsEnabled)
                    {
                        activeCount++;
                        Logger.LogInfo($"[Fastranauts] Module enabled and patched: {module.ModuleName}");
                    }
                    else
                    {
                        Logger.LogInfo($"[Fastranauts] Module disabled via config: {module.ModuleName}");
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogError($"[Fastranauts] Failed to initialize module {module.ModuleName} (isolated safely): {ex}");
                }
            }

            TryPatchConsoleCommand();

            Logger.LogInfo($"[Fastranauts] v{PluginVersion} initialized ({activeCount}/{_modules.Count} modules active).");
        }

        private void Update()
        {
            if (_telemetryKeyConfig != null && _telemetryKeyConfig.Value != KeyCode.None && Input.GetKeyDown(_telemetryKeyConfig.Value))
            {
                string report = FastStats.BuildSummaryReport();
                Logger.LogInfo("\n" + report);
            }
        }

        private void TryPatchConsoleCommand()
        {
            try
            {
                MethodInfo resolveString = AccessTools.Method(typeof(ConsoleResolver), nameof(ConsoleResolver.ResolveString), new[] { typeof(string).MakeByRefType() });
                if (resolveString != null)
                {
                    _harmony.Patch(
                        resolveString,
                        prefix: new HarmonyMethod(typeof(FastranautsPlugin), nameof(ConsoleResolver_ResolveString_Prefix)));
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"[Fastranauts] Could not bind 'fastinfo' console command: {ex.Message}");
            }
        }

        public static bool ConsoleResolver_ResolveString_Prefix(ref string __0, ref bool __result)
        {
            if (string.IsNullOrEmpty(__0))
            {
                return true;
            }

            string trimmed = __0.Trim();
            if (string.Equals(trimmed, "fastinfo", StringComparison.OrdinalIgnoreCase))
            {
                string report = FastStats.BuildSummaryReport();
                if (Instance != null)
                {
                    Instance.Logger.LogInfo("\n" + report);
                }
                __0 = "fastinfo\n" + report;
                __result = true;
                return false;
            }

            return true;
        }
    }
}
