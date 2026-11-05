using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx.Configuration;
using BepInEx.Logging;
using Fastranauts.Common;
using HarmonyLib;
using Ostranauts.Pathing;
using UnityEngine;
using PathVector2Int = Ostranauts.Pathing.Vector2Int;

namespace Fastranauts.Modules
{
    /// <summary>
    /// Fixes Pathfinding (JumpPointSearch / PathMemory) and AI Radar Tracking bottlenecks:
    ///
    /// 1. JumpPointSearch.GetGridSize allocates a brand-new bool?[_gridXMax, _gridYMax] 2D array (10KB–80KB)
    ///    on every single path query. Replaced via Transpiler with a reusable cleared 2D buffer.
    /// 2. Ostranauts.Pathing.Vector2Int is a custom struct without IEquatable&lt;Vector2Int&gt; or GetHashCode()/Equals()
    ///    overrides, causing every HashSet&lt;Vector2Int&gt; and Dictionary&lt;Vector2Int, Node&gt; operation in JPS
    ///    and PathMemory to box the struct into System.ValueType and use reflection hashing.
    ///    Fixed via FastVector2IntComparer and reusable collections in JumpPointSearch.FindPath and PathMemory.
    /// 3. AIShipManager.CheckTracking iterates all regional AI ships every single frame (60–144 Hz) to update
    ///    the radar warning indicator. Throttled to 5 Hz (0.20s interval).
    /// </summary>
    public sealed class PathfindingAndAiOptimizationModule : IFastModule
    {
        public string ModuleName => "PathfindingAndAiOptimization";
        public bool IsEnabled => _enabledConfig != null && _enabledConfig.Value;

        private ConfigEntry<bool> _enabledConfig;
        private ConfigEntry<float> _radarTrackingIntervalConfig;
        private static ManualLogSource _logger;

        private static float _radarTrackingInterval = 0.20f;
        private static float _lastRadarTrackingTime;

        private static bool?[,] _sharedWalkableGrid;
        private static readonly HashSet<PathVector2Int> _sharedClosedSet =
            new HashSet<PathVector2Int>(FastVector2IntComparer.Instance);
        private static readonly Dictionary<PathVector2Int, Node> _sharedNodeMap =
            new Dictionary<PathVector2Int, Node>(256, FastVector2IntComparer.Instance);

        private static AccessTools.FieldRef<PathMemory, Dictionary<PathVector2Int, PathResult>> _searchedPathsRef;

        public void Initialize(ConfigFile config, ManualLogSource logger, Harmony harmony)
        {
            _logger = logger;
            _enabledConfig = config.Bind(
                "06. Pathfinding & AI",
                "EnablePathfindingAndAiOptimization",
                true,
                "Reuses JumpPointSearch 2D walkable grids, eliminates Vector2Int struct boxing in JPS/PathMemory, and rate-limits AI radar tracking scans.");

            _radarTrackingIntervalConfig = config.Bind(
                "06. Pathfinding & AI",
                "RadarTrackingIntervalSeconds",
                0.20f,
                new ConfigDescription(
                    "Interval (seconds) between AIShipManager.CheckTracking radar warning scans.",
                    new AcceptableValueRange<float>(0.05f, 1.0f)));

            if (!IsEnabled)
            {
                return;
            }

            _radarTrackingInterval = _radarTrackingIntervalConfig.Value;

            MethodInfo getGridSize = AccessTools.Method(typeof(JumpPointSearch), "GetGridSize");
            if (getGridSize != null)
            {
                harmony.Patch(
                    getGridSize,
                    transpiler: new HarmonyMethod(typeof(PathfindingAndAiOptimizationModule), nameof(JumpPointSearch_GetGridSize_Transpiler)));
            }

            MethodInfo findPath = AccessTools.Method(typeof(JumpPointSearch), "FindPath");
            if (findPath != null)
            {
                harmony.Patch(
                    findPath,
                    transpiler: new HarmonyMethod(typeof(PathfindingAndAiOptimizationModule), nameof(JumpPointSearch_FindPath_Transpiler)));
            }

            try
            {
                _searchedPathsRef = AccessTools.FieldRefAccess<PathMemory, Dictionary<PathVector2Int, PathResult>>("SearchedPaths");
                ConstructorInfo pathMemoryCtor = AccessTools.Constructor(typeof(PathMemory), Type.EmptyTypes);
                if (pathMemoryCtor != null && _searchedPathsRef != null)
                {
                    harmony.Patch(
                        pathMemoryCtor,
                        postfix: new HarmonyMethod(typeof(PathfindingAndAiOptimizationModule), nameof(PathMemory_Ctor_Postfix)));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"[{ModuleName}] Could not patch PathMemory comparer: {ex.Message}");
            }

            MethodInfo checkTracking = AccessTools.Method(typeof(AIShipManager), "CheckTracking");
            if (checkTracking != null)
            {
                harmony.Patch(
                    checkTracking,
                    prefix: new HarmonyMethod(typeof(PathfindingAndAiOptimizationModule), nameof(AIShipManager_CheckTracking_Prefix)));
            }
        }

        public static bool?[,] GetReusableWalkableGrid(int xMax, int yMax)
        {
            FastStats.JpsGridsReused++;
            if (xMax <= 0) xMax = 1;
            if (yMax <= 0) yMax = 1;

            if (_sharedWalkableGrid == null ||
                _sharedWalkableGrid.GetLength(0) < xMax ||
                _sharedWalkableGrid.GetLength(1) < yMax)
            {
                int allocX = Math.Max(xMax, _sharedWalkableGrid != null ? _sharedWalkableGrid.GetLength(0) : 128);
                int allocY = Math.Max(yMax, _sharedWalkableGrid != null ? _sharedWalkableGrid.GetLength(1) : 128);
                _sharedWalkableGrid = new bool?[allocX, allocY];
                return _sharedWalkableGrid;
            }

            Array.Clear(_sharedWalkableGrid, 0, _sharedWalkableGrid.Length);
            return _sharedWalkableGrid;
        }

        public static HashSet<PathVector2Int> GetClearedClosedSet()
        {
            _sharedClosedSet.Clear();
            return _sharedClosedSet;
        }

        public static Dictionary<PathVector2Int, Node> GetClearedNodeMap()
        {
            _sharedNodeMap.Clear();
            return _sharedNodeMap;
        }

        public static void PathMemory_Ctor_Postfix(PathMemory __instance)
        {
            if (__instance != null && _searchedPathsRef != null)
            {
                _searchedPathsRef(__instance) = new Dictionary<PathVector2Int, PathResult>(32, FastVector2IntComparer.Instance);
            }
        }

        public static bool AIShipManager_CheckTracking_Prefix()
        {
            float now = Time.unscaledTime;
            if (now - _lastRadarTrackingTime < _radarTrackingInterval)
            {
                FastStats.RadarTrackingChecksThrottled++;
                return false;
            }
            _lastRadarTrackingTime = now;
            return true;
        }

        public static IEnumerable<CodeInstruction> JumpPointSearch_GetGridSize_Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            ConstructorInfo gridCtor = AccessTools.Constructor(typeof(bool?[,]), new[] { typeof(int), typeof(int) });
            MethodInfo getReusableGrid = AccessTools.Method(typeof(PathfindingAndAiOptimizationModule), nameof(GetReusableWalkableGrid));

            foreach (CodeInstruction inst in instructions)
            {
                if (gridCtor != null && getReusableGrid != null && inst.opcode == OpCodes.Newobj && Equals(inst.operand, gridCtor))
                {
                    yield return new CodeInstruction(OpCodes.Call, getReusableGrid).WithLabels(inst.labels);
                    continue;
                }
                yield return inst;
            }
        }

        public static IEnumerable<CodeInstruction> JumpPointSearch_FindPath_Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            ConstructorInfo hashSetCtor = AccessTools.Constructor(typeof(HashSet<PathVector2Int>), Type.EmptyTypes);
            ConstructorInfo dictCtor = AccessTools.Constructor(typeof(Dictionary<PathVector2Int, Node>), Type.EmptyTypes);

            MethodInfo getClosedSet = AccessTools.Method(typeof(PathfindingAndAiOptimizationModule), nameof(GetClearedClosedSet));
            MethodInfo getNodeMap = AccessTools.Method(typeof(PathfindingAndAiOptimizationModule), nameof(GetClearedNodeMap));

            foreach (CodeInstruction inst in instructions)
            {
                if (inst.opcode == OpCodes.Newobj)
                {
                    if (hashSetCtor != null && getClosedSet != null && Equals(inst.operand, hashSetCtor))
                    {
                        yield return new CodeInstruction(OpCodes.Call, getClosedSet).WithLabels(inst.labels);
                        continue;
                    }
                    if (dictCtor != null && getNodeMap != null && Equals(inst.operand, dictCtor))
                    {
                        yield return new CodeInstruction(OpCodes.Call, getNodeMap).WithLabels(inst.labels);
                        continue;
                    }
                }
                yield return inst;
            }
        }

        /// <summary>
        /// Custom equality comparer for Ostranauts.Pathing.Vector2Int that avoids ValueType.GetHashCode()
        /// reflection and struct boxing inside Dictionary and HashSet lookups.
        /// </summary>
        public sealed class FastVector2IntComparer : IEqualityComparer<PathVector2Int>
        {
            public static readonly FastVector2IntComparer Instance = new FastVector2IntComparer();

            public bool Equals(PathVector2Int a, PathVector2Int b)
            {
                return a.x == b.x && a.y == b.y;
            }

            public int GetHashCode(PathVector2Int obj)
            {
                unchecked
                {
                    return (obj.x * 397) ^ obj.y;
                }
            }
        }
    }
}
