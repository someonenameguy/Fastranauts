using System;
using System.Collections.Generic;
using System.Linq;
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
    /// Fixes Bottleneck #2 from the runtime profiler:
    /// PlayState &gt; CrewSim::Update (3.80 ms | 22,072 B Mem) &amp; CondOwner GC churn.
    ///
    /// 1. Replaces Physics.RaycastAll (and subsequent OrderBy/ToArray LINQ allocations) in
    ///    Ship.GetCOsAtWorldCoords1, CrewSim.GetMouseOverCO, and CrewSim.GetMouseOverBG with
    ///    FastRaycast.RaycastAll (Physics.RaycastNonAlloc + in-place distance sort + zero-alloc enumerable).
    /// 2. Replaces new List&lt;string&gt;(dictRecentlyTried.Keys) in CondOwner.Cleanup with a zero-allocation
    ///    expired-entry sweep that also fixes the vanilla single-item-per-cycle memory leak bug.
    /// 3. Replaces new MaterialPropertyBlock() in CondOwner.SetHighlight with a reusable MaterialPropertyBlock.
    /// 4. Interns strName.Substring(6) in CondOwner.GetCondAmount, HasCond, and AddCondAmount for threshold lookups.
    /// </summary>
    public sealed class RaycastAndGcOptimizationModule : IFastModule
    {
        public string ModuleName => "RaycastAndGcOptimization";
        public bool IsEnabled => _enabledConfig != null && _enabledConfig.Value;

        private ConfigEntry<bool> _enabledConfig;
        private static ManualLogSource _logger;

        private static readonly List<string> _sharedExpiredKeys = new List<string>(32);
        private static readonly List<string> _emptyStringList = new List<string>(0);
        private static readonly Dictionary<string, string> _thresholdSubstringCache =
            new Dictionary<string, string>(512, StringComparer.Ordinal);
        private static MaterialPropertyBlock _sharedHighlightPropertyBlock;

        public void Initialize(ConfigFile config, ManualLogSource logger, Harmony harmony)
        {
            _logger = logger;
            _enabledConfig = config.Bind(
                "03. Raycasts & GC Memory",
                "EnableRaycastAndGcOptimization",
                true,
                "Replaces Physics.RaycastAll with RaycastNonAlloc in hot paths, fixes CondOwner.Cleanup memory leak & list allocation, reuses MaterialPropertyBlock on hover, and caches threshold substrings.");

            if (!IsEnabled)
            {
                return;
            }

            // 1. Physics.RaycastAll -> FastRaycast.RaycastAll in Ship.GetCOsAtWorldCoords1, CrewSim.GetMouseOverCO, CrewSim.GetMouseOverBG
            MethodInfo getCOsAtWorldCoords1 = AccessTools.Method(
                typeof(Ship),
                nameof(Ship.GetCOsAtWorldCoords1),
                new[] { typeof(Vector2), typeof(CondTrigger), typeof(bool), typeof(bool), typeof(List<CondOwner>) });
            if (getCOsAtWorldCoords1 != null)
            {
                harmony.Patch(
                    getCOsAtWorldCoords1,
                    transpiler: new HarmonyMethod(typeof(RaycastAndGcOptimizationModule), nameof(RaycastAll_Transpiler)));
            }

            MethodInfo getMouseOverCO = AccessTools.Method(typeof(CrewSim), "GetMouseOverCO");
            if (getMouseOverCO != null)
            {
                harmony.Patch(
                    getMouseOverCO,
                    transpiler: new HarmonyMethod(typeof(RaycastAndGcOptimizationModule), nameof(RaycastAll_Transpiler)));
            }

            MethodInfo getMouseOverBG = AccessTools.Method(typeof(CrewSim), "GetMouseOverBG");
            if (getMouseOverBG != null)
            {
                harmony.Patch(
                    getMouseOverBG,
                    transpiler: new HarmonyMethod(typeof(RaycastAndGcOptimizationModule), nameof(RaycastAll_Transpiler)));
            }

            // 2. CondOwner.Cleanup zero-allocation & leak fix
            MethodInfo cleanupMethod = AccessTools.Method(typeof(CondOwner), "Cleanup");
            if (cleanupMethod != null)
            {
                harmony.Patch(
                    cleanupMethod,
                    transpiler: new HarmonyMethod(typeof(RaycastAndGcOptimizationModule), nameof(CondOwner_Cleanup_Transpiler)));
            }

            // 3. CondOwner.SetHighlight reusable MaterialPropertyBlock
            MethodInfo setHighlightMethod = AccessTools.Method(typeof(CondOwner), nameof(CondOwner.SetHighlight), new[] { typeof(float) });
            if (setHighlightMethod != null)
            {
                harmony.Patch(
                    setHighlightMethod,
                    transpiler: new HarmonyMethod(typeof(RaycastAndGcOptimizationModule), nameof(CondOwner_SetHighlight_Transpiler)));
            }

            // 4. Threshold substring pooling in CondOwner.GetCondAmount, HasCond, AddCondAmount
            MethodInfo getCondAmount = AccessTools.Method(typeof(CondOwner), nameof(CondOwner.GetCondAmount), new[] { typeof(string), typeof(bool) });
            if (getCondAmount != null)
            {
                harmony.Patch(
                    getCondAmount,
                    transpiler: new HarmonyMethod(typeof(RaycastAndGcOptimizationModule), nameof(ThresholdSubstring_Transpiler)));
            }

            MethodInfo hasCond = AccessTools.Method(typeof(CondOwner), nameof(CondOwner.HasCond), new[] { typeof(string), typeof(bool) });
            if (hasCond != null)
            {
                harmony.Patch(
                    hasCond,
                    transpiler: new HarmonyMethod(typeof(RaycastAndGcOptimizationModule), nameof(ThresholdSubstring_Transpiler)));
            }

            MethodInfo addCondAmount = AccessTools.Method(
                typeof(CondOwner),
                nameof(CondOwner.AddCondAmount),
                new[] { typeof(string), typeof(double), typeof(double), typeof(float) });
            if (addCondAmount != null)
            {
                harmony.Patch(
                    addCondAmount,
                    transpiler: new HarmonyMethod(typeof(RaycastAndGcOptimizationModule), nameof(ThresholdSubstring_Transpiler)));
            }
        }

        /// <summary>
        /// Replaces new List&lt;string&gt;(dictRecentlyTried.Keys) in CondOwner.Cleanup().
        /// Performs the full >60s expiration sweep in-place using a reusable buffer (fixing the vanilla
        /// single-item break bug) and returns an empty list so the subsequent foreach loop is a fast no-op.
        /// </summary>
        public static List<string> CleanRecentlyTriedZeroAlloc(IEnumerable<string> keysCollection)
        {
            FastStats.RecentlyTriedCleanupsOptimized++;

            // keysCollection is Dictionary<string, double>.KeyCollection
            // However, to remove from the dictionary directly, we handle it in CleanupRecentlyTriedDict if possible,
            // or populate _sharedExpiredKeys for the vanilla loop.
            _sharedExpiredKeys.Clear();
            if (keysCollection != null)
            {
                foreach (string key in keysCollection)
                {
                    _sharedExpiredKeys.Add(key);
                }
            }
            return _sharedExpiredKeys;
        }

        public static void SweepAllExpiredRecentlyTried(Dictionary<string, double> dict)
        {
            if (dict == null || dict.Count == 0)
            {
                return;
            }

            double now = StarSystem.fEpoch;
            _sharedExpiredKeys.Clear();
            foreach (KeyValuePair<string, double> kvp in dict)
            {
                if (now - kvp.Value > 60.0)
                {
                    _sharedExpiredKeys.Add(kvp.Key);
                }
            }

            int expiredCount = _sharedExpiredKeys.Count;
            for (int i = 0; i < expiredCount; i++)
            {
                dict.Remove(_sharedExpiredKeys[i]);
            }
            _sharedExpiredKeys.Clear();
        }

        public static List<string> SweepAndReturnEmptyKeyList(Dictionary<string, double> dict)
        {
            FastStats.RecentlyTriedCleanupsOptimized++;
            SweepAllExpiredRecentlyTried(dict);
            return _emptyStringList;
        }

        public static MaterialPropertyBlock GetSharedHighlightPropertyBlock()
        {
            if (_sharedHighlightPropertyBlock == null)
            {
                _sharedHighlightPropertyBlock = new MaterialPropertyBlock();
            }
            else
            {
                _sharedHighlightPropertyBlock.Clear();
            }
            return _sharedHighlightPropertyBlock;
        }

        public static string SubstringCached(string str, int startIndex)
        {
            if (str == null)
            {
                return null;
            }
            if (startIndex == 6)
            {
                FastStats.ThresholdSubstringsCached++;
                if (!_thresholdSubstringCache.TryGetValue(str, out string sub))
                {
                    sub = str.Substring(6);
                    _thresholdSubstringCache[str] = sub;
                }
                return sub;
            }
            return str.Substring(startIndex);
        }

        public static IEnumerable<CodeInstruction> RaycastAll_Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo raycastAll2 = AccessTools.Method(typeof(Physics), nameof(Physics.RaycastAll), new[] { typeof(Ray), typeof(float) });
            MethodInfo raycastAll3 = AccessTools.Method(typeof(Physics), nameof(Physics.RaycastAll), new[] { typeof(Ray), typeof(float), typeof(int) });

            MethodInfo fastRaycast2 = AccessTools.Method(typeof(FastRaycast), nameof(FastRaycast.RaycastAll), new[] { typeof(Ray), typeof(float) });
            MethodInfo fastRaycast3 = AccessTools.Method(typeof(FastRaycast), nameof(FastRaycast.RaycastAll), new[] { typeof(Ray), typeof(float), typeof(int) });

            MethodInfo orderByGeneric = null;
            MethodInfo toArrayGeneric = null;
            foreach (MethodInfo m in typeof(Enumerable).GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                if (m.Name == nameof(Enumerable.OrderBy) && m.GetParameters().Length == 2)
                {
                    orderByGeneric = m.MakeGenericMethod(typeof(RaycastHit), typeof(float));
                }
                else if (m.Name == nameof(Enumerable.ToArray) && m.GetParameters().Length == 1)
                {
                    toArrayGeneric = m.MakeGenericMethod(typeof(RaycastHit));
                }
            }

            MethodInfo passThroughOrderBy = AccessTools.Method(typeof(FastRaycast), nameof(FastRaycast.PassThroughOrderBy));
            MethodInfo toArrayZeroAlloc = AccessTools.Method(typeof(FastRaycast), nameof(FastRaycast.ToArrayZeroAlloc));

            foreach (CodeInstruction inst in instructions)
            {
                if (raycastAll2 != null && fastRaycast2 != null && inst.Calls(raycastAll2))
                {
                    yield return new CodeInstruction(OpCodes.Call, fastRaycast2).WithLabels(inst.labels);
                    continue;
                }

                if (raycastAll3 != null && fastRaycast3 != null && inst.Calls(raycastAll3))
                {
                    yield return new CodeInstruction(OpCodes.Call, fastRaycast3).WithLabels(inst.labels);
                    continue;
                }

                if (orderByGeneric != null && passThroughOrderBy != null && inst.Calls(orderByGeneric))
                {
                    yield return new CodeInstruction(OpCodes.Call, passThroughOrderBy).WithLabels(inst.labels);
                    continue;
                }

                if (toArrayGeneric != null && toArrayZeroAlloc != null && inst.Calls(toArrayGeneric))
                {
                    yield return new CodeInstruction(OpCodes.Call, toArrayZeroAlloc).WithLabels(inst.labels);
                    continue;
                }

                yield return inst;
            }
        }

        public static IEnumerable<CodeInstruction> CondOwner_Cleanup_Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            List<CodeInstruction> list = new List<CodeInstruction>(instructions);

            MethodInfo getKeysGetter = AccessTools.PropertyGetter(typeof(Dictionary<string, double>), nameof(Dictionary<string, double>.Keys));
            ConstructorInfo listStringCtor = AccessTools.Constructor(typeof(List<string>), new[] { typeof(IEnumerable<string>) });
            MethodInfo sweepHelper = AccessTools.Method(typeof(RaycastAndGcOptimizationModule), nameof(SweepAndReturnEmptyKeyList));

            for (int i = 0; i < list.Count - 1; i++)
            {
                // Match: callvirt Dictionary<string, double>::get_Keys + newobj List<string>(IEnumerable<string>)
                if (getKeysGetter != null && listStringCtor != null && sweepHelper != null &&
                    list[i].Calls(getKeysGetter) &&
                    list[i + 1].opcode == OpCodes.Newobj && Equals(list[i + 1].operand, listStringCtor))
                {
                    list[i] = new CodeInstruction(OpCodes.Call, sweepHelper).WithLabels(list[i].labels);
                    list[i + 1] = new CodeInstruction(OpCodes.Nop).WithLabels(list[i + 1].labels);
                    break;
                }
            }

            return list;
        }

        public static IEnumerable<CodeInstruction> CondOwner_SetHighlight_Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            ConstructorInfo mpbCtor = AccessTools.Constructor(typeof(MaterialPropertyBlock), Type.EmptyTypes);
            MethodInfo getSharedMpb = AccessTools.Method(typeof(RaycastAndGcOptimizationModule), nameof(GetSharedHighlightPropertyBlock));

            foreach (CodeInstruction inst in instructions)
            {
                if (mpbCtor != null && getSharedMpb != null && inst.opcode == OpCodes.Newobj && Equals(inst.operand, mpbCtor))
                {
                    yield return new CodeInstruction(OpCodes.Call, getSharedMpb).WithLabels(inst.labels);
                    continue;
                }
                yield return inst;
            }
        }

        public static IEnumerable<CodeInstruction> ThresholdSubstring_Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo substring1 = AccessTools.Method(typeof(string), nameof(string.Substring), new[] { typeof(int) });
            MethodInfo substringCached = AccessTools.Method(typeof(RaycastAndGcOptimizationModule), nameof(SubstringCached));

            foreach (CodeInstruction inst in instructions)
            {
                if (substring1 != null && substringCached != null && inst.Calls(substring1))
                {
                    yield return new CodeInstruction(OpCodes.Call, substringCached).WithLabels(inst.labels);
                    continue;
                }
                yield return inst;
            }
        }
    }
}
