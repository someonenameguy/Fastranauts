using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using BepInEx.Configuration;
using BepInEx.Logging;
using Fastranauts.Common;
using HarmonyLib;
using Ostranauts.COCommands;
using Ostranauts.Core.Models;
using UnityEngine;

namespace Fastranauts.Modules
{
    /// <summary>
    /// Optimizes the simulation sub-stepping loop, CondOwner ticker updates, and epoch-based
    /// MonoBehaviour.Update() simulation components across Pause and High-Speed (2x-16x) modes:
    ///
    /// 1. Smart Sub-Stepping in CrewSim.Update:
    ///    Replaces the rigid 4ms (0.004s) minimum sub-step floor with an adaptive floor scaled by
    ///    Time.timeScale and capped at 12 sub-steps per rendered frame (preventing 70-130+ micro-steps
    ///    per frame at 16x speed when an unsorted ticker has fNextTickerSecs &lt;= 0.004).
    /// 2. Zero-Allocation Filtered CrewSim.UpdateICOs:
    ///    Eliminates aTickersTemp.AddRange(CrewSim.aTickers) boxed IEnumerator allocations and skips
    ///    CondOwner.UpdateManual(10) on the ~98% of CondOwners whose earliest ticker has not expired
    ///    (fNextTickerSecs &gt; 0.0) and are not due for real-time-throttled Cleanup().
    /// 3. Once-Per-Frame Visual &amp; Stat Updates in CondOwner.UpdateManual:
    ///    Defers RefreshAnim(), UpdateStats(), and Item.VisualizeOverlays(false) so they run at most
    ///    once per rendered frame per CondOwner instead of repeating on every sub-step.
    /// 4. Fast Undamaged Item Stat &amp; Damage Rate Lookups:
    ///    Avoids repeated FetchDamageConds() dictionary lookups and "100%" string assignments in
    ///    CondOwner.UpdateStats() and CondOwner.GetDamageRate() when an item has no "StatDamage".
    /// 5. Pause-Guards &amp; Real-Time Unscaled Throttling for Epoch Simulation Components:
    ///    Skips simulation MonoBehaviour.Update() calls while CrewSim.Paused is true, and caps their
    ///    real-time polling frequency during 2x-16x speed-up while preserving exact StarSystem.fEpoch
    ///    elapsed time math. Also skips useless Room self-comparisons in GasContainer.CheckPressureDifference().
    /// </summary>
    public sealed class UpdateDecouplingAndSimTickModule : IFastModule
    {
        public string ModuleName => "UpdateDecouplingAndSimTick";
        public bool IsEnabled => _enabledConfig != null && _enabledConfig.Value;

        private ConfigEntry<bool> _enabledConfig;
        private static ManualLogSource _logger;

        // Cached reflection & fast field delegates for CrewSim / CondOwner / GasContainer
        private static FieldInfo _aTickersField;
        private static UniqueList<CondOwner> _cachedTickersList;
        private static AccessTools.FieldRef<CrewSim, float> _fUIUpdateLastRef;
        private static AccessTools.FieldRef<CrewSim, float> _fUIUpdateHeartbeatRef;
        private static AccessTools.FieldRef<CondOwner, double> _fLastCleanupRef;
        private static AccessTools.FieldRef<GasContainer, CondOwner> _gasContainerCoRef;
        private static AccessTools.FieldRef<GasContainer, bool> _gasContainerRequiresInitRef;
        private static Action<CondOwner> _updateStatsDelegate;

        // Reusable zero-allocation buffers for CrewSim.UpdateICOs
        private static readonly List<CondOwner> _dueTickersBuffer = new List<CondOwner>(128);
        private static readonly List<CondOwner> _removalQueue = new List<CondOwner>(32);
        private static float _lastGlobalCleanupRealTime = -10f;

        // Per-frame visual update deduplication for CondOwner.UpdateManual
        private static readonly HashSet<CondOwner> _updatedVisualsThisFrame = new HashSet<CondOwner>();
        private static int _lastVisualFrameCount = -1;
        private static bool _currentCondOwnerSkipVisuals;

        // Real-time unscaled throttling state for epoch-driven simulation components at >1x speed
        private static readonly Dictionary<int, float> _lastRealUpdateByInstanceId = new Dictionary<int, float>(4096);
        private static float _lastCachePurgeUnscaledTime;

        public void Initialize(ConfigFile config, ManualLogSource logger, Harmony harmony)
        {
            _logger = logger;
            _enabledConfig = config.Bind(
                "08. Update & Time Speed System",
                "EnableUpdateDecouplingAndSimTick",
                true,
                "Optimizes CrewSim sub-stepping, filters idle tickers in UpdateICOs, limits CondOwner visual updates to once per rendered frame, and throttles epoch-based simulation Updates during Pause and 2x-16x speed.");

            if (!IsEnabled)
            {
                return;
            }

            InitializeFieldRefs();

            // 1. Smart Sub-Stepping in CrewSim.Update & Filtered CrewSim.UpdateICOs
            MethodInfo crewSimUpdate = AccessTools.Method(typeof(CrewSim), "Update");
            if (crewSimUpdate != null)
            {
                harmony.Patch(
                    crewSimUpdate,
                    transpiler: new HarmonyMethod(typeof(UpdateDecouplingAndSimTickModule), nameof(CrewSim_Update_Transpiler)));
            }

            MethodInfo updateICOs = AccessTools.Method(typeof(CrewSim), "UpdateICOs");
            if (updateICOs != null && _aTickersField != null && _fUIUpdateLastRef != null && _fUIUpdateHeartbeatRef != null && _fLastCleanupRef != null)
            {
                harmony.Patch(
                    updateICOs,
                    transpiler: new HarmonyMethod(typeof(UpdateDecouplingAndSimTickModule), nameof(CrewSim_UpdateICOs_Transpiler)));
            }

            // 2. Once-per-frame post-ticker visuals in CondOwner.UpdateManual & Undamaged Stat Fast-Paths
            MethodInfo coUpdateManual = AccessTools.Method(typeof(CondOwner), nameof(CondOwner.UpdateManual), new[] { typeof(int) });
            if (coUpdateManual != null)
            {
                harmony.Patch(
                    coUpdateManual,
                    transpiler: new HarmonyMethod(typeof(UpdateDecouplingAndSimTickModule), nameof(CondOwner_UpdateManual_Transpiler)));
            }

            MethodInfo getDamageRate = AccessTools.Method(typeof(CondOwner), nameof(CondOwner.GetDamageRate), Type.EmptyTypes);
            if (getDamageRate != null)
            {
                harmony.Patch(
                    getDamageRate,
                    prefix: new HarmonyMethod(typeof(UpdateDecouplingAndSimTickModule), nameof(CondOwner_GetDamageRate_Prefix)));
            }

            MethodInfo updateStats = AccessTools.Method(typeof(CondOwner), "UpdateStats", Type.EmptyTypes);
            if (updateStats != null)
            {
                harmony.Patch(
                    updateStats,
                    prefix: new HarmonyMethod(typeof(UpdateDecouplingAndSimTickModule), nameof(CondOwner_UpdateStats_Prefix)));
            }

            // 3. Pause-Guards & High-Speed Real-Time Throttling for Simulation MonoBehaviour.Update() Methods
            PatchMethodPrefix(harmony, typeof(BodyTemp), "Update", nameof(BodyTemp_Update_Prefix));
            PatchMethodPrefix(harmony, typeof(Heater), "Update", nameof(Heater_Update_Prefix));
            PatchMethodPrefix(harmony, typeof(Wound), "Update", nameof(Wound_Update_Prefix));
            PatchMethodPrefix(harmony, typeof(Sensor), "Update", nameof(Sensor_Update_Prefix));
            PatchMethodPrefix(harmony, typeof(GasPump), "Update", nameof(GasPump_Update_Prefix));
            PatchMethodPrefix(harmony, typeof(GasExchange), "Update", nameof(GasExchange_Update_Prefix));
            PatchMethodPrefix(harmony, typeof(Powered), "Update", nameof(Powered_Update_Prefix));
            PatchMethodPrefix(harmony, typeof(FusionIC), "Update", nameof(FusionIC_Update_Prefix));
            PatchMethodPrefix(harmony, typeof(Trader), "Update", nameof(Trader_Update_Prefix));
            PatchMethodPrefix(harmony, typeof(Rotor), "Update", nameof(Rotor_Update_Prefix));
            PatchMethodPrefix(harmony, typeof(Rotor), nameof(Rotor.UpdateManual), nameof(Rotor_UpdateManual_Prefix));
            PatchMethodPrefix(harmony, typeof(GasPressureSense), "Update", nameof(GasPressureSense_Update_Prefix));
            PatchMethodPrefix(harmony, typeof(GasContainer), "Update", nameof(GasContainer_Update_Prefix));

            MethodInfo checkPressureDiffInstance = AccessTools.Method(typeof(GasContainer), "CheckPressureDifference", Type.EmptyTypes);
            if (checkPressureDiffInstance != null)
            {
                harmony.Patch(
                    checkPressureDiffInstance,
                    prefix: new HarmonyMethod(typeof(UpdateDecouplingAndSimTickModule), nameof(GasContainer_CheckPressureDifference_Prefix)));
            }
        }

        private static void InitializeFieldRefs()
        {
            try
            {
                _aTickersField = AccessTools.Field(typeof(CrewSim), "aTickers");
                _fUIUpdateLastRef = AccessTools.FieldRefAccess<CrewSim, float>("fUIUpdateLast");
                _fUIUpdateHeartbeatRef = AccessTools.FieldRefAccess<CrewSim, float>("fUIUpdateHeartbeat");
                _fLastCleanupRef = AccessTools.FieldRefAccess<CondOwner, double>("fLastCleanup");
                _gasContainerCoRef = AccessTools.FieldRefAccess<GasContainer, CondOwner>("co");
                _gasContainerRequiresInitRef = AccessTools.FieldRefAccess<GasContainer, bool>("bRequiresInit");

                MethodInfo updateStatsMethod = AccessTools.Method(typeof(CondOwner), "UpdateStats", Type.EmptyTypes);
                if (updateStatsMethod != null)
                {
                    _updateStatsDelegate = (Action<CondOwner>)Delegate.CreateDelegate(typeof(Action<CondOwner>), updateStatsMethod);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning($"[Fastranauts] UpdateDecouplingAndSimTickModule field ref init warning: {ex.Message}");
            }
        }

        private static void PatchMethodPrefix(Harmony harmony, Type targetType, string methodName, string prefixMethodName)
        {
            MethodInfo target = AccessTools.Method(targetType, methodName, Type.EmptyTypes);
            MethodInfo prefix = AccessTools.Method(typeof(UpdateDecouplingAndSimTickModule), prefixMethodName);
            if (target != null && prefix != null)
            {
                harmony.Patch(target, prefix: new HarmonyMethod(prefix));
            }
        }

        // =====================================================================
        // 1. Smart Sub-Stepping & Zero-Allocation Filtered CrewSim.UpdateICOs
        // =====================================================================

        public static double GetAdaptiveMinSubstepSeconds()
        {
            double totalFrameSimSeconds = CrewSim.Paused ? 0.0 : (double)Time.deltaTime;
            return GetAdaptiveMinSubstepSeconds(totalFrameSimSeconds);
        }

        public static double GetAdaptiveMinSubstepSeconds(double totalFrameSimSeconds)
        {
            double scaledFloor = 0.004 * Math.Max(1.0, (double)Time.timeScale * 0.5);
            return Math.Max(scaledFloor, totalFrameSimSeconds / 12.0);
        }

        public static IEnumerable<CodeInstruction> CrewSim_Update_Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo adaptiveFloorMethod = AccessTools.Method(
                typeof(UpdateDecouplingAndSimTickModule),
                nameof(GetAdaptiveMinSubstepSeconds),
                Type.EmptyTypes);

            foreach (CodeInstruction inst in instructions)
            {
                if (adaptiveFloorMethod != null &&
                    inst.opcode == OpCodes.Ldc_R8 &&
                    inst.operand is double val &&
                    Math.Abs(val - 0.004) < 1e-9)
                {
                    yield return new CodeInstruction(OpCodes.Call, adaptiveFloorMethod).WithLabels(inst.labels);
                    continue;
                }

                yield return inst;
            }
        }

        public static IEnumerable<CodeInstruction> CrewSim_UpdateICOs_Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo fastMethod = AccessTools.Method(
                typeof(UpdateDecouplingAndSimTickModule),
                nameof(CrewSim_UpdateICOs_Fast),
                new[] { typeof(CrewSim) });

            if (fastMethod == null)
            {
                foreach (CodeInstruction inst in instructions)
                {
                    yield return inst;
                }
                yield break;
            }

            yield return new CodeInstruction(OpCodes.Ldarg_0);
            yield return new CodeInstruction(OpCodes.Call, fastMethod);
            yield return new CodeInstruction(OpCodes.Ret);
        }

        public static void CrewSim_UpdateICOs_Fast(CrewSim __instance)
        {
            CondOwner.nEndTurnsThisFrame = 0;

            UniqueList<CondOwner> tickers = _cachedTickersList;
            if (tickers == null && _aTickersField != null)
            {
                tickers = _aTickersField.GetValue(null) as UniqueList<CondOwner>;
                _cachedTickersList = tickers;
            }

            if (tickers != null)
            {
                int count = tickers.Count;
                if (count > 0)
                {
                    _removalQueue.Clear();
                    _dueTickersBuffer.Clear();

                    double epoch = StarSystem.fEpoch;
                    float unscaledNow = Time.unscaledTime;
                    bool allowCleanupThisStep = (unscaledNow - _lastGlobalCleanupRealTime) >= 0.5f;
                    bool anyCleanupQueued = false;

                    for (int i = 0; i < count; i++)
                    {
                        CondOwner co = tickers[i];
                        if (co == null || co.ship == null || co.ship.bDestroyed || !co.ship.gameObject.activeInHierarchy)
                        {
                            _removalQueue.Add(co);
                        }
                        else if (co.fNextTickerSecs <= 0.0)
                        {
                            _dueTickersBuffer.Add(co);
                        }
                        else if (allowCleanupThisStep && (epoch - _fLastCleanupRef(co) > 2.0))
                        {
                            _dueTickersBuffer.Add(co);
                            anyCleanupQueued = true;
                        }
                        else
                        {
                            FastStats.IdleTickerSubstepsSkipped++;
                        }
                    }

                    if (anyCleanupQueued)
                    {
                        _lastGlobalCleanupRealTime = unscaledNow;
                    }

                    int removeCount = _removalQueue.Count;
                    if (removeCount > 0)
                    {
                        for (int i = 0; i < removeCount; i++)
                        {
                            CrewSim.RemoveTicker(_removalQueue[i]);
                        }
                        _removalQueue.Clear();
                    }

                    int dueCount = _dueTickersBuffer.Count;
                    if (dueCount > 0)
                    {
                        for (int i = 0; i < dueCount; i++)
                        {
                            CondOwner co = _dueTickersBuffer[i];
                            if (co != null && !co.bDestroyed && co.ship != null && !co.ship.bDestroyed && co.ship.gameObject.activeInHierarchy)
                            {
                                co.UpdateManual(10);
                            }
                        }
                        _dueTickersBuffer.Clear();
                    }
                }
            }

            float totalGameSec = CrewSim.fTotalGameSec;
            if (totalGameSec - _fUIUpdateLastRef(__instance) > _fUIUpdateHeartbeatRef(__instance))
            {
                _fUIUpdateLastRef(__instance) = totalGameSec;
            }
        }

        // =====================================================================
        // 2. Once-Per-Frame Visuals in CondOwner.UpdateManual & Fast Stats
        // =====================================================================

        public static void RefreshAnimSubstepGated(CondOwner co)
        {
            if (co == null)
            {
                _currentCondOwnerSkipVisuals = true;
                return;
            }

            int frame = Time.frameCount;
            if (frame != _lastVisualFrameCount)
            {
                _lastVisualFrameCount = frame;
                _updatedVisualsThisFrame.Clear();
            }

            if (!_updatedVisualsThisFrame.Add(co))
            {
                _currentCondOwnerSkipVisuals = true;
                FastStats.SubstepVisualUpdatesDeferred++;
                return;
            }

            _currentCondOwnerSkipVisuals = false;
            co.RefreshAnim();
        }

        public static void UpdateStatsSubstepGated(CondOwner co)
        {
            if (_currentCondOwnerSkipVisuals || co == null)
            {
                return;
            }

            UpdateStatsFast(co);
        }

        public static void VisualizeOverlaysSubstepGated(Item item, bool bForce)
        {
            if (_currentCondOwnerSkipVisuals || item == null)
            {
                return;
            }

            item.VisualizeOverlays(bForce);
        }

        public static void UpdateStatsFast(CondOwner co)
        {
            if (co.mapConds != null && !co.mapConds.ContainsKey("StatDamage"))
            {
                FastStats.UndamagedStatLookupsSaved++;
                if (co.mapInfo != null && (!co.mapInfo.TryGetValue("Condition", out string existing) || existing != "100%"))
                {
                    co.mapInfo["Condition"] = "100%";
                }
                return;
            }

            _updateStatsDelegate?.Invoke(co);
        }

        public static IEnumerable<CodeInstruction> CondOwner_UpdateManual_Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo refreshAnimOrig = AccessTools.Method(typeof(CondOwner), nameof(CondOwner.RefreshAnim), Type.EmptyTypes);
            MethodInfo updateStatsOrig = AccessTools.Method(typeof(CondOwner), "UpdateStats", Type.EmptyTypes);
            MethodInfo visualizeOverlaysOrig = AccessTools.Method(typeof(Item), nameof(Item.VisualizeOverlays), new[] { typeof(bool) });

            MethodInfo refreshAnimGated = AccessTools.Method(typeof(UpdateDecouplingAndSimTickModule), nameof(RefreshAnimSubstepGated));
            MethodInfo updateStatsGated = AccessTools.Method(typeof(UpdateDecouplingAndSimTickModule), nameof(UpdateStatsSubstepGated));
            MethodInfo visualizeOverlaysGated = AccessTools.Method(typeof(UpdateDecouplingAndSimTickModule), nameof(VisualizeOverlaysSubstepGated));

            foreach (CodeInstruction inst in instructions)
            {
                if (refreshAnimOrig != null && refreshAnimGated != null && inst.Calls(refreshAnimOrig))
                {
                    yield return new CodeInstruction(OpCodes.Call, refreshAnimGated).WithLabels(inst.labels);
                    continue;
                }

                if (updateStatsOrig != null && updateStatsGated != null && inst.Calls(updateStatsOrig))
                {
                    yield return new CodeInstruction(OpCodes.Call, updateStatsGated).WithLabels(inst.labels);
                    continue;
                }

                if (visualizeOverlaysOrig != null && visualizeOverlaysGated != null && inst.Calls(visualizeOverlaysOrig))
                {
                    yield return new CodeInstruction(OpCodes.Call, visualizeOverlaysGated).WithLabels(inst.labels);
                    continue;
                }

                yield return inst;
            }
        }

        public static bool CondOwner_GetDamageRate_Prefix(CondOwner __instance, ref float __result)
        {
            if (__instance.mapConds != null && !__instance.mapConds.ContainsKey("StatDamage"))
            {
                __result = 0f;
                FastStats.UndamagedStatLookupsSaved++;
                return false;
            }
            return true;
        }

        public static bool CondOwner_UpdateStats_Prefix(CondOwner __instance)
        {
            if (__instance.mapConds != null && !__instance.mapConds.ContainsKey("StatDamage"))
            {
                FastStats.UndamagedStatLookupsSaved++;
                if (__instance.mapInfo != null && (!__instance.mapInfo.TryGetValue("Condition", out string existing) || existing != "100%"))
                {
                    __instance.mapInfo["Condition"] = "100%";
                }
                return false;
            }
            return true;
        }

        // =====================================================================
        // 3. Pause-Guard & Real-Time-Throttle Simulation MonoBehaviour.Update()
        // =====================================================================

        public static bool ShouldRunEpochSimUpdate(object instance, float minUnscaledIntervalAtHighSpeed)
        {
            if (CrewSim.Paused)
            {
                FastStats.PausedUpdatesSkipped++;
                return false;
            }

            if (Time.timeScale > 1.01f && instance != null)
            {
                if (!CheckAndRecordRealTimeInterval(instance, minUnscaledIntervalAtHighSpeed))
                {
                    FastStats.SimComponentUpdatesThrottled++;
                    return false;
                }
            }

            return true;
        }

        private static bool CheckAndRecordRealTimeInterval(object instance, float minUnscaledInterval)
        {
            float now = Time.unscaledTime;
            if (_lastRealUpdateByInstanceId.Count > 16384 || (now - _lastCachePurgeUnscaledTime) > 120f)
            {
                _lastRealUpdateByInstanceId.Clear();
                _lastCachePurgeUnscaledTime = now;
            }

            int id = RuntimeHelpers.GetHashCode(instance);
            if (_lastRealUpdateByInstanceId.TryGetValue(id, out float lastTime))
            {
                if ((now - lastTime) < minUnscaledInterval)
                {
                    return false;
                }
                _lastRealUpdateByInstanceId[id] = now;
                return true;
            }

            // Stagger initial timestamp across 8 buckets so components don't all tick on the same frame
            float staggerOffset = (id & 7) * 0.015f;
            _lastRealUpdateByInstanceId[id] = now - staggerOffset;
            return true;
        }

        public static bool BodyTemp_Update_Prefix(BodyTemp __instance)
        {
            return ShouldRunEpochSimUpdate(__instance, 0.15f);
        }

        public static bool Heater_Update_Prefix(Heater __instance)
        {
            if (!CrewSim.Paused && __instance != null && __instance.bUpdateRemote)
            {
                return true;
            }
            return ShouldRunEpochSimUpdate(__instance, 0.15f);
        }

        public static bool Wound_Update_Prefix(Wound __instance)
        {
            return ShouldRunEpochSimUpdate(__instance, 0.20f);
        }

        public static bool Sensor_Update_Prefix(Sensor __instance)
        {
            return ShouldRunEpochSimUpdate(__instance, 0.15f);
        }

        public static bool GasPump_Update_Prefix(GasPump __instance)
        {
            if (!CrewSim.Paused && __instance != null && __instance.bUpdateRemote)
            {
                return true;
            }
            return ShouldRunEpochSimUpdate(__instance, 0.12f);
        }

        public static bool GasExchange_Update_Prefix(GasExchange __instance)
        {
            return ShouldRunEpochSimUpdate(__instance, 0.10f);
        }

        public static bool Powered_Update_Prefix(Powered __instance)
        {
            return ShouldRunEpochSimUpdate(__instance, 0.15f);
        }

        public static bool FusionIC_Update_Prefix(FusionIC __instance)
        {
            if (CrewSim.Paused)
            {
                FastStats.PausedUpdatesSkipped++;
                return false;
            }

            // Keep fDeltaTime below 1.0 sim second so FusionIC.Run's num12 clamp (fDeltaTime > 1.0) is never hit
            float maxSafeUnscaledInterval = Math.Min(0.10f, 0.85f / Math.Max(1f, Time.timeScale));
            return ShouldRunEpochSimUpdate(__instance, maxSafeUnscaledInterval);
        }

        public static bool Trader_Update_Prefix(Trader __instance)
        {
            return ShouldRunEpochSimUpdate(__instance, 0.25f);
        }

        public static bool Rotor_Update_Prefix()
        {
            if (CrewSim.Paused)
            {
                FastStats.PausedUpdatesSkipped++;
                return false;
            }
            return true;
        }

        public static bool Rotor_UpdateManual_Prefix(Rotor __instance)
        {
            return ShouldRunEpochSimUpdate(__instance, 0.15f);
        }

        public static bool GasPressureSense_Update_Prefix()
        {
            if (CrewSim.Paused)
            {
                FastStats.PausedUpdatesSkipped++;
                return false;
            }
            return true;
        }

        public static bool GasContainer_Update_Prefix(GasContainer __instance)
        {
            if (!CrewSim.Paused)
            {
                return true;
            }

            if (_gasContainerCoRef != null && _gasContainerRequiresInitRef != null)
            {
                CondOwner co = _gasContainerCoRef(__instance);
                if (co != null && !_gasContainerRequiresInitRef(__instance) && !co.GasChanged && __instance.fDGasTemp == 0.0)
                {
                    FastStats.PausedUpdatesSkipped++;
                    return false;
                }
            }

            return true;
        }

        public static bool GasContainer_CheckPressureDifference_Prefix(GasContainer __instance)
        {
            if (CrewSim.Paused)
            {
                FastStats.SimComponentUpdatesThrottled++;
                return false;
            }

            if (_gasContainerCoRef != null)
            {
                CondOwner co = _gasContainerCoRef(__instance);
                if (co == null || co.ship == null)
                {
                    return false;
                }

                // A room's GasContainer comparing pressure against the room at its own position compares against itself (diff == 0)
                if (co.HasCond("IsRoom"))
                {
                    FastStats.SimComponentUpdatesThrottled++;
                    return false;
                }
            }

            if (Time.timeScale > 1.01f && !CheckAndRecordRealTimeInterval(__instance, 0.25f))
            {
                FastStats.SimComponentUpdatesThrottled++;
                return false;
            }

            return true;
        }
    }
}
