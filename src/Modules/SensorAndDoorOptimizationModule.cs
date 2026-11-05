using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx.Configuration;
using BepInEx.Logging;
using Fastranauts.Common;
using HarmonyLib;

namespace Fastranauts.Modules
{
    /// <summary>
    /// Fixes Bottleneck #3 from the runtime profiler:
    /// ItmDoor01OpenOn &gt; GasPressureSense::Update (0.21 ms | 2,219 B per door)
    /// and GasPump / GasContainer.AddGasMols allocations.
    ///
    /// 1. Replaces new List&lt;CondOwner&gt;() and DataHandler.GetCondTrigger(strCTClear) (which clones CondTrigger)
    ///    in GasPressureSense.Run with a reusable static List&lt;CondOwner&gt; and non-cloning GetCondTrigger(name, false).
    /// 2. Replaces new List&lt;CondOwner&gt;() in GasPump.GetA, GasPump.GetB, and GasPump.Respire2 with reusable lists.
    /// 3. Eliminates "StatGasMol" + strGas string concatenation and full Condition object instantiation inside
    ///    GasContainer.AddGasMols when checking if a gas condition definition exists.
    /// </summary>
    public sealed class SensorAndDoorOptimizationModule : IFastModule
    {
        public string ModuleName => "SensorAndDoorOptimization";
        public bool IsEnabled => _enabledConfig != null && _enabledConfig.Value;

        private ConfigEntry<bool> _enabledConfig;
        private static ManualLogSource _logger;

        private static readonly List<CondOwner> _sharedDoorSensorList = new List<CondOwner>(16);
        private static readonly List<CondOwner> _sharedPumpListA = new List<CondOwner>(16);
        private static readonly List<CondOwner> _sharedPumpListB = new List<CondOwner>(16);
        private static readonly List<CondOwner> _sharedRespireList = new List<CondOwner>(16);

        private static readonly Dictionary<string, string> _statGasMolNameCache =
            new Dictionary<string, string>(32, StringComparer.Ordinal);

        private static Condition _validCondMarker;

        public void Initialize(ConfigFile config, ManualLogSource logger, Harmony harmony)
        {
            _logger = logger;
            _enabledConfig = config.Bind(
                "02. Sensors & Atmosphere",
                "EnableSensorAndDoorOptimization",
                true,
                "Eliminates per-tick List<CondOwner> and CondTrigger clone allocations in GasPressureSense (doors), GasPump, and GasContainer.AddGasMols.");

            if (!IsEnabled)
            {
                return;
            }

            MethodInfo senseRun = AccessTools.Method(typeof(GasPressureSense), "Run");
            if (senseRun != null)
            {
                harmony.Patch(
                    senseRun,
                    transpiler: new HarmonyMethod(typeof(SensorAndDoorOptimizationModule), nameof(GasPressureSense_Run_Transpiler)));
            }

            MethodInfo pumpGetA = AccessTools.Method(typeof(GasPump), "GetA");
            if (pumpGetA != null)
            {
                harmony.Patch(
                    pumpGetA,
                    transpiler: new HarmonyMethod(typeof(SensorAndDoorOptimizationModule), nameof(GasPump_GetA_Transpiler)));
            }

            MethodInfo pumpGetB = AccessTools.Method(typeof(GasPump), "GetB");
            if (pumpGetB != null)
            {
                harmony.Patch(
                    pumpGetB,
                    transpiler: new HarmonyMethod(typeof(SensorAndDoorOptimizationModule), nameof(GasPump_GetB_Transpiler)));
            }

            MethodInfo pumpRespire2 = AccessTools.Method(typeof(GasPump), "Respire2");
            if (pumpRespire2 != null)
            {
                harmony.Patch(
                    pumpRespire2,
                    transpiler: new HarmonyMethod(typeof(SensorAndDoorOptimizationModule), nameof(GasPump_Respire2_Transpiler)));
            }

            MethodInfo addGasMols = AccessTools.Method(
                typeof(GasContainer),
                nameof(GasContainer.AddGasMols),
                new[] { typeof(string), typeof(double), typeof(bool) });
            if (addGasMols != null)
            {
                harmony.Patch(
                    addGasMols,
                    transpiler: new HarmonyMethod(typeof(SensorAndDoorOptimizationModule), nameof(GasContainer_AddGasMols_Transpiler)));
            }
        }

        public static List<CondOwner> GetClearedDoorSensorList()
        {
            FastStats.SensorZeroAllocListsUsed++;
            _sharedDoorSensorList.Clear();
            return _sharedDoorSensorList;
        }

        public static List<CondOwner> GetClearedPumpListA()
        {
            FastStats.SensorZeroAllocListsUsed++;
            _sharedPumpListA.Clear();
            return _sharedPumpListA;
        }

        public static List<CondOwner> GetClearedPumpListB()
        {
            FastStats.SensorZeroAllocListsUsed++;
            _sharedPumpListB.Clear();
            return _sharedPumpListB;
        }

        public static List<CondOwner> GetClearedRespireList()
        {
            FastStats.SensorZeroAllocListsUsed++;
            _sharedRespireList.Clear();
            return _sharedRespireList;
        }

        public static CondTrigger GetCondTriggerNoClone(string strName)
        {
            return DataHandler.GetCondTrigger(strName, false);
        }

        public static string ConcatStatGasMolCached(string prefix, string strGas)
        {
            if (strGas == null)
            {
                return prefix;
            }
            if (!_statGasMolNameCache.TryGetValue(strGas, out string full))
            {
                full = string.Concat(prefix, strGas);
                _statGasMolNameCache[strGas] = full;
            }
            return full;
        }

        public static Condition GetCondExistenceMarker(string strName)
        {
            if (strName == null || DataHandler.dictConds == null)
            {
                return null;
            }
            if (DataHandler.dictConds.TryGetValue(strName, out JsonCond jsonCond) && jsonCond != null)
            {
                FastStats.GasAddMolsCondAllocSaved++;
                if (_validCondMarker == null)
                {
                    _validCondMarker = new Condition(jsonCond);
                }
                return _validCondMarker;
            }
            return null;
        }

        public static IEnumerable<CodeInstruction> GasPressureSense_Run_Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            ConstructorInfo listCtor = AccessTools.Constructor(typeof(List<CondOwner>), Type.EmptyTypes);
            MethodInfo getSharedList = AccessTools.Method(typeof(SensorAndDoorOptimizationModule), nameof(GetClearedDoorSensorList));

            MethodInfo getCondTrigger1 = AccessTools.Method(typeof(DataHandler), nameof(DataHandler.GetCondTrigger), new[] { typeof(string) });
            MethodInfo getCondTriggerFast = AccessTools.Method(typeof(SensorAndDoorOptimizationModule), nameof(GetCondTriggerNoClone));

            foreach (CodeInstruction inst in instructions)
            {
                if (listCtor != null && getSharedList != null && inst.opcode == OpCodes.Newobj && Equals(inst.operand, listCtor))
                {
                    yield return new CodeInstruction(OpCodes.Call, getSharedList).WithLabels(inst.labels);
                    continue;
                }

                if (getCondTrigger1 != null && getCondTriggerFast != null && inst.Calls(getCondTrigger1))
                {
                    yield return new CodeInstruction(OpCodes.Call, getCondTriggerFast).WithLabels(inst.labels);
                    continue;
                }

                yield return inst;
            }
        }

        public static IEnumerable<CodeInstruction> GasPump_GetA_Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            return ReplaceCondOwnerListCtor(instructions, AccessTools.Method(typeof(SensorAndDoorOptimizationModule), nameof(GetClearedPumpListA)));
        }

        public static IEnumerable<CodeInstruction> GasPump_GetB_Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            return ReplaceCondOwnerListCtor(instructions, AccessTools.Method(typeof(SensorAndDoorOptimizationModule), nameof(GetClearedPumpListB)));
        }

        public static IEnumerable<CodeInstruction> GasPump_Respire2_Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            return ReplaceCondOwnerListCtor(instructions, AccessTools.Method(typeof(SensorAndDoorOptimizationModule), nameof(GetClearedRespireList)));
        }

        private static IEnumerable<CodeInstruction> ReplaceCondOwnerListCtor(IEnumerable<CodeInstruction> instructions, MethodInfo helperMethod)
        {
            ConstructorInfo listCtor = AccessTools.Constructor(typeof(List<CondOwner>), Type.EmptyTypes);
            foreach (CodeInstruction inst in instructions)
            {
                if (listCtor != null && helperMethod != null && inst.opcode == OpCodes.Newobj && Equals(inst.operand, listCtor))
                {
                    yield return new CodeInstruction(OpCodes.Call, helperMethod).WithLabels(inst.labels);
                    continue;
                }
                yield return inst;
            }
        }

        public static IEnumerable<CodeInstruction> GasContainer_AddGasMols_Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo stringConcat2 = AccessTools.Method(typeof(string), nameof(string.Concat), new[] { typeof(string), typeof(string) });
            MethodInfo concatCached = AccessTools.Method(typeof(SensorAndDoorOptimizationModule), nameof(ConcatStatGasMolCached));

            MethodInfo getCondMethod = AccessTools.Method(typeof(DataHandler), nameof(DataHandler.GetCond), new[] { typeof(string) });
            MethodInfo getCondMarker = AccessTools.Method(typeof(SensorAndDoorOptimizationModule), nameof(GetCondExistenceMarker));

            foreach (CodeInstruction inst in instructions)
            {
                if (stringConcat2 != null && concatCached != null && inst.Calls(stringConcat2))
                {
                    yield return new CodeInstruction(OpCodes.Call, concatCached).WithLabels(inst.labels);
                    continue;
                }

                if (getCondMethod != null && getCondMarker != null && inst.Calls(getCondMethod))
                {
                    yield return new CodeInstruction(OpCodes.Call, getCondMarker).WithLabels(inst.labels);
                    continue;
                }

                yield return inst;
            }
        }
    }
}
