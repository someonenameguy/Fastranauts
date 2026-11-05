using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx.Configuration;
using BepInEx.Logging;
using Fastranauts.Common;
using HarmonyLib;
using UnityEngine;

namespace Fastranauts.Modules
{
    /// <summary>
    /// Fixes Bottleneck #1 from the runtime profiler:
    /// pnlViz &gt; PDAVisualisers::Update (24.20 ms | 211,306 B Mem every 0.5s, and 30.00 ms at 16x while PAUSED).
    ///
    /// 1. Transpiles PDAVisualisers.Update so _pingTime uses GetPdaPingDeltaTime():
    ///    - Returns 0f while CrewSim.Paused is true (completely preventing periodic NotifyShips() sweeps while paused,
    ///      while still allowing immediate bNotifyShips refreshes when the player clicks a preset/slider while paused).
    ///    - Returns Time.unscaledDeltaTime when unpaused so 16x simulation speed does not accelerate PDA shader sweeps
    ///      from 2 Hz to 32 Hz.
    /// 2. Distinguishes periodic 0.5s _pingTime refreshes from explicit user overlay preset/slider changes
    ///    and passes force = false into Ship.VisualizeOverlays on periodic pings so Item.VisualizeOverlays
    ///    uses its built-in epsilon dirty check instead of calling GetPropertyBlock/SetPropertyBlock on
    ///    thousands of unchanged items, and throttles Default ("_None") periodic sweeps to 0.5 Hz real time.
    /// 3. Transpiles Ship.VisualizeOverlays to avoid cloning the "Blank" CondTrigger and to use a
    ///    zero-allocation CondOwner collector instead of allocating HashSet&lt;CondOwner&gt;, CondOwner[], and List&lt;CondOwner&gt;.
    /// 4. Bypasses wrapping CondOwnerVisitor in a redundant CondOwnerVisitorCondTrigger when the trigger is "Blank".
    /// </summary>
    public sealed class PdaOverlayOptimizationModule : IFastModule
    {
        public string ModuleName => "PdaOverlayOptimization";
        public bool IsEnabled => _enabledConfig != null && _enabledConfig.Value;

        private ConfigEntry<bool> _enabledConfig;
        private static ManualLogSource _logger;

        private static AccessTools.FieldRef<PDAVisualisers, bool> _bNotifyShipsRef;
        private static bool _isPeriodicPing;
        private static float _lastDefaultSweepUnscaledTime = -10f;

        private static readonly CondOwnerVisitorAddToHashSet _sharedVisitor = new CondOwnerVisitorAddToHashSet();
        private static readonly List<CondOwner> _sharedOverlayCoList = new List<CondOwner>(2048);

        public void Initialize(ConfigFile config, ManualLogSource logger, Harmony harmony)
        {
            _logger = logger;
            _enabledConfig = config.Bind(
                "01. PDA Overlays",
                "EnablePdaOverlayOptimization",
                true,
                "Eliminates the 24ms / 211KB periodic frame spike every 0.5s (and 30ms spike at 16x while paused) when PDA status overlays are active.");

            if (!IsEnabled)
            {
                return;
            }

            try
            {
                _bNotifyShipsRef = AccessTools.FieldRefAccess<PDAVisualisers, bool>("bNotifyShips");
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"[{ModuleName}] Could not bind bNotifyShips field ref: {ex.Message}");
            }

            try
            {
                MethodInfo pdaUpdateMethod = AccessTools.Method(typeof(PDAVisualisers), "Update");
                if (pdaUpdateMethod != null)
                {
                    harmony.Patch(
                        pdaUpdateMethod,
                        transpiler: new HarmonyMethod(typeof(PdaOverlayOptimizationModule), nameof(PDAVisualisers_Update_Transpiler)));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"[{ModuleName}] Failed to patch PDAVisualisers.Update: {ex.Message}");
            }

            MethodInfo notifyShipsMethod = AccessTools.Method(typeof(PDAVisualisers), "NotifyShips");
            if (notifyShipsMethod != null && _bNotifyShipsRef != null)
            {
                harmony.Patch(
                    notifyShipsMethod,
                    prefix: new HarmonyMethod(typeof(PdaOverlayOptimizationModule), nameof(NotifyShips_Prefix)),
                    postfix: new HarmonyMethod(typeof(PdaOverlayOptimizationModule), nameof(NotifyShips_Postfix)));
            }

            MethodInfo shipVizMethod = AccessTools.Method(typeof(Ship), nameof(Ship.VisualizeOverlays), new[] { typeof(bool) });
            if (shipVizMethod != null)
            {
                harmony.Patch(
                    shipVizMethod,
                    prefix: new HarmonyMethod(typeof(PdaOverlayOptimizationModule), nameof(Ship_VisualizeOverlays_Prefix)),
                    transpiler: new HarmonyMethod(typeof(PdaOverlayOptimizationModule), nameof(Ship_VisualizeOverlays_Transpiler)));
            }

            MethodInfo wrapVisitorMethod = AccessTools.Method(
                typeof(CondOwnerVisitorCondTrigger),
                nameof(CondOwnerVisitorCondTrigger.WrapVisitor),
                new[] { typeof(CondOwnerVisitor), typeof(CondTrigger) });
            if (wrapVisitorMethod != null)
            {
                harmony.Patch(
                    wrapVisitorMethod,
                    prefix: new HarmonyMethod(typeof(PdaOverlayOptimizationModule), nameof(WrapVisitor_Prefix)));
            }
        }

        /// <summary>
        /// Replaces Time.deltaTime in PDAVisualisers.Update so _pingTime does not advance while CrewSim.Paused
        /// and uses Time.unscaledDeltaTime when unpaused (preventing 16x timeScale from running sweeps at 32 Hz).
        /// </summary>
        public static float GetPdaPingDeltaTime()
        {
            if (CrewSim.Paused)
            {
                FastStats.PdaPausedOrDefaultSkips++;
                return 0f;
            }

            FastStats.UnscaledUiTimersDecoupled++;
            return Time.unscaledDeltaTime;
        }

        public static IEnumerable<CodeInstruction> PDAVisualisers_Update_Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo getDeltaTime = AccessTools.PropertyGetter(typeof(Time), nameof(Time.deltaTime));
            MethodInfo getPdaPingDeltaTime = AccessTools.Method(typeof(PdaOverlayOptimizationModule), nameof(GetPdaPingDeltaTime));

            foreach (CodeInstruction inst in instructions)
            {
                if (getDeltaTime != null && getPdaPingDeltaTime != null && inst.Calls(getDeltaTime))
                {
                    yield return new CodeInstruction(OpCodes.Call, getPdaPingDeltaTime).WithLabels(inst.labels);
                    continue;
                }

                yield return inst;
            }
        }

        public static void NotifyShips_Prefix(PDAVisualisers __instance)
        {
            try
            {
                // bNotifyShips is true when the user explicitly selects a preset or changes sliders/toggles,
                // and false when NotifyShips() is called by the periodic 0.5s _pingTime refresh in Update().
                _isPeriodicPing = _bNotifyShipsRef != null && !__instance.Equals(null) && !_bNotifyShipsRef(__instance);
            }
            catch
            {
                _isPeriodicPing = false;
            }
        }

        public static void NotifyShips_Postfix()
        {
            _isPeriodicPing = false;
        }

        public static bool Ship_VisualizeOverlays_Prefix(ref bool __0)
        {
            if (_isPeriodicPing)
            {
                if (CrewSim.Paused)
                {
                    FastStats.PdaPausedOrDefaultSkips++;
                    return false;
                }

                if (GUIPDA.instance != null &&
                    GUIPDA.instance.pdaVisualisers != null &&
                    GUIPDA.instance.pdaVisualisers.OverlayVariable == "_None")
                {
                    float unscaledNow = Time.unscaledTime;
                    if (unscaledNow > _lastDefaultSweepUnscaledTime && unscaledNow - _lastDefaultSweepUnscaledTime < 2.0f)
                    {
                        FastStats.PdaPausedOrDefaultSkips++;
                        return false;
                    }
                    _lastDefaultSweepUnscaledTime = unscaledNow;
                }

                if (__0)
                {
                    __0 = false;
                    FastStats.PdaForcedOverlaySkips++;
                }
            }
            return true;
        }

        public static bool WrapVisitor_Prefix(CondOwnerVisitor __0, CondTrigger __1, ref CondOwnerVisitor __result)
        {
            if (__1 != null && __1.strName == "Blank")
            {
                __result = __0;
                FastStats.BlankTriggerWrapperBypassed++;
                return false;
            }
            return true;
        }

        public static CondTrigger GetCondTriggerNoClone(string strName)
        {
            return DataHandler.GetCondTrigger(strName, false);
        }

        public static List<CondOwner> GetShipCOsForOverlayFast(
            Ship ship,
            CondTrigger objCondTrig,
            bool bSubObjects,
            bool bAllowDocked,
            bool bAllowLocked)
        {
            FastStats.PdaZeroAllocOverlayQueries++;
            _sharedOverlayCoList.Clear();

            if (ship == null)
            {
                return _sharedOverlayCoList;
            }

            // If a non-Blank trigger or docked search was requested, fall back safely
            if ((objCondTrig != null && objCondTrig.strName != "Blank") || bAllowDocked)
            {
                return ship.GetCOs(objCondTrig, bSubObjects, bAllowDocked, bAllowLocked);
            }

            Dictionary<string, CondOwner> mapICOs = ship.GetMappedCos();
            if (mapICOs == null)
            {
                return _sharedOverlayCoList;
            }

            HashSet<CondOwner> visited = _sharedVisitor.aHashSet;
            visited.Clear();

            foreach (KeyValuePair<string, CondOwner> kvp in mapICOs)
            {
                CondOwner co = kvp.Value;
                if (co != null && co.objCOParent == null)
                {
                    if (bSubObjects)
                    {
                        co.VisitCOs(_sharedVisitor, bAllowLocked);
                    }
                    _sharedVisitor.Visit(co);
                }
            }

            if (ship.aRooms != null)
            {
                int roomCount = ship.aRooms.Count;
                for (int i = 0; i < roomCount; i++)
                {
                    Room room = ship.aRooms[i];
                    if (room != null && room.CO != null)
                    {
                        _sharedVisitor.Visit(room.CO);
                    }
                }
            }

            foreach (CondOwner co in visited)
            {
                _sharedOverlayCoList.Add(co);
            }

            visited.Clear();
            return _sharedOverlayCoList;
        }

        public static IEnumerable<CodeInstruction> Ship_VisualizeOverlays_Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo getCondTrigger1 = AccessTools.Method(typeof(DataHandler), nameof(DataHandler.GetCondTrigger), new[] { typeof(string) });
            MethodInfo getCondTriggerFast = AccessTools.Method(typeof(PdaOverlayOptimizationModule), nameof(GetCondTriggerNoClone));

            MethodInfo shipGetCOs = AccessTools.Method(
                typeof(Ship),
                nameof(Ship.GetCOs),
                new[] { typeof(CondTrigger), typeof(bool), typeof(bool), typeof(bool) });
            MethodInfo getShipCOsFast = AccessTools.Method(typeof(PdaOverlayOptimizationModule), nameof(GetShipCOsForOverlayFast));

            foreach (CodeInstruction inst in instructions)
            {
                if (getCondTrigger1 != null && getCondTriggerFast != null && inst.Calls(getCondTrigger1))
                {
                    yield return new CodeInstruction(OpCodes.Call, getCondTriggerFast).WithLabels(inst.labels);
                    continue;
                }

                if (shipGetCOs != null && getShipCOsFast != null && inst.Calls(shipGetCOs))
                {
                    yield return new CodeInstruction(OpCodes.Call, getShipCOsFast).WithLabels(inst.labels);
                    continue;
                }

                yield return inst;
            }
        }
    }
}
