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
    /// Fixes Bottleneck #5 from the runtime profiler:
    /// ArmLLamp &gt; Visibility::LateUpdate (0.06 ms | 715–933 B Mem per lamp/crew light every frame).
    ///
    /// 1. Replaces the 5 per-frame List&lt;T&gt; allocations in Visibility.ResetLightMesh (shadowVerts,
    ///    shadowIndices, shadowUVs, skirt, skirtW) with dedicated cleared static List&lt;T&gt; buffers.
    /// 2. Replaces new List&lt;Segment&gt;() in Visibility.AddOccludersFromCrewSimBlocks and
    ///    Visibility.IsCondOwnerLOSVisibleBlocks with persistent static List&lt;Segment&gt; buffers so
    ///    Block.GetSegments reuses its 4 Segment instances as intended instead of allocating 4 new
    ///    Segment objects on every block check.
    /// 3. Replaces new List&lt;Vector2&gt;() in Visibility.EmitSkirt with a cleared static buffer.
    /// 4. Replaces shadowVerts.ToArray(), shadowIndices.ToArray(), and shadowUVs.ToArray() in
    ///    Visibility.LateUpdate with zero-allocation Mesh.SetVertices(List), Mesh.SetTriangles(List, 0),
    ///    and Mesh.SetUVs(0, List).
    /// </summary>
    public sealed class VisibilityLightOptimizationModule : IFastModule
    {
        public string ModuleName => "VisibilityLightOptimization";
        public bool IsEnabled => _enabledConfig != null && _enabledConfig.Value;

        private ConfigEntry<bool> _enabledConfig;
        private static ManualLogSource _logger;

        private static readonly List<Vector3> _sharedShadowVerts = new List<Vector3>(512);
        private static readonly List<int> _sharedShadowIndices = new List<int>(1536);
        private static readonly List<Vector2> _sharedShadowUVs = new List<Vector2>(512);
        private static readonly List<Vector2> _sharedSkirt = new List<Vector2>(256);
        private static readonly List<float> _sharedSkirtW = new List<float>(128);
        private static readonly List<Vector2> _sharedSkirtTemp = new List<Vector2>(256);

        // Kept with 4 Segment elements so Block.GetSegments(ref list) reuses them without allocating new Segment()
        private static readonly List<Segment> _sharedOccluderSegments = new List<Segment>(4);
        private static readonly List<Segment> _sharedLosSegments = new List<Segment>(4);

        public void Initialize(ConfigFile config, ManualLogSource logger, Harmony harmony)
        {
            _logger = logger;
            _enabledConfig = config.Bind(
                "04. Lighting & Visibility",
                "EnableVisibilityLightOptimization",
                true,
                "Eliminates per-frame List<T>, Segment, and Mesh.ToArray() allocations in Visibility light/LOS calculations.");

            if (!IsEnabled)
            {
                return;
            }

            MethodInfo resetLightMesh = AccessTools.Method(typeof(Visibility), "ResetLightMesh");
            if (resetLightMesh != null)
            {
                harmony.Patch(
                    resetLightMesh,
                    transpiler: new HarmonyMethod(typeof(VisibilityLightOptimizationModule), nameof(ResetLightMesh_Transpiler)));
            }

            MethodInfo addOccluders = AccessTools.Method(typeof(Visibility), "AddOccludersFromCrewSimBlocks");
            if (addOccluders != null)
            {
                harmony.Patch(
                    addOccluders,
                    transpiler: new HarmonyMethod(typeof(VisibilityLightOptimizationModule), nameof(AddOccludersFromCrewSimBlocks_Transpiler)));
            }

            MethodInfo losBlocks = AccessTools.Method(
                typeof(Visibility),
                nameof(Visibility.IsCondOwnerLOSVisibleBlocks),
                new[] { typeof(CondOwner), typeof(Vector2), typeof(bool), typeof(bool) });
            if (losBlocks != null)
            {
                harmony.Patch(
                    losBlocks,
                    transpiler: new HarmonyMethod(typeof(VisibilityLightOptimizationModule), nameof(IsCondOwnerLOSVisibleBlocks_Transpiler)));
            }

            MethodInfo emitSkirt = AccessTools.Method(typeof(Visibility), "EmitSkirt");
            if (emitSkirt != null)
            {
                harmony.Patch(
                    emitSkirt,
                    transpiler: new HarmonyMethod(typeof(VisibilityLightOptimizationModule), nameof(EmitSkirt_Transpiler)));
            }

            MethodInfo lateUpdate = AccessTools.Method(typeof(Visibility), "LateUpdate");
            if (lateUpdate != null)
            {
                harmony.Patch(
                    lateUpdate,
                    transpiler: new HarmonyMethod(typeof(VisibilityLightOptimizationModule), nameof(LateUpdate_Transpiler)));
            }
        }

        public static List<Vector3> GetClearedShadowVerts()
        {
            _sharedShadowVerts.Clear();
            return _sharedShadowVerts;
        }

        public static List<int> GetClearedShadowIndices()
        {
            _sharedShadowIndices.Clear();
            return _sharedShadowIndices;
        }

        public static List<Vector2> GetClearedShadowUVs()
        {
            _sharedShadowUVs.Clear();
            return _sharedShadowUVs;
        }

        public static List<Vector2> GetClearedSkirt()
        {
            _sharedSkirt.Clear();
            return _sharedSkirt;
        }

        public static List<float> GetClearedSkirtW()
        {
            _sharedSkirtW.Clear();
            return _sharedSkirtW;
        }

        public static List<Vector2> GetClearedSkirtTemp()
        {
            _sharedSkirtTemp.Clear();
            return _sharedSkirtTemp;
        }

        public static List<Segment> GetReusableOccluderSegments()
        {
            return _sharedOccluderSegments;
        }

        public static List<Segment> GetReusableLosSegments()
        {
            return _sharedLosSegments;
        }

        public static void SetMeshVerticesFast(Mesh mesh, List<Vector3> verts)
        {
            FastStats.VisibilityMeshesZeroAlloc++;
            mesh.SetVertices(verts);
        }

        public static void SetMeshTrianglesFast(Mesh mesh, List<int> indices)
        {
            mesh.SetTriangles(indices, 0);
        }

        public static void SetMeshUVsFast(Mesh mesh, List<Vector2> uvs)
        {
            mesh.SetUVs(0, uvs);
        }

        public static IEnumerable<CodeInstruction> ResetLightMesh_Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            ConstructorInfo vec3ListCtor = AccessTools.Constructor(typeof(List<Vector3>), Type.EmptyTypes);
            ConstructorInfo intListCtor = AccessTools.Constructor(typeof(List<int>), Type.EmptyTypes);
            ConstructorInfo vec2ListCtor = AccessTools.Constructor(typeof(List<Vector2>), Type.EmptyTypes);
            ConstructorInfo floatListCtor = AccessTools.Constructor(typeof(List<float>), Type.EmptyTypes);

            MethodInfo getShadowVerts = AccessTools.Method(typeof(VisibilityLightOptimizationModule), nameof(GetClearedShadowVerts));
            MethodInfo getShadowIndices = AccessTools.Method(typeof(VisibilityLightOptimizationModule), nameof(GetClearedShadowIndices));
            MethodInfo getShadowUVs = AccessTools.Method(typeof(VisibilityLightOptimizationModule), nameof(GetClearedShadowUVs));
            MethodInfo getSkirt = AccessTools.Method(typeof(VisibilityLightOptimizationModule), nameof(GetClearedSkirt));
            MethodInfo getSkirtW = AccessTools.Method(typeof(VisibilityLightOptimizationModule), nameof(GetClearedSkirtW));

            int vec2CtorOccurrence = 0;

            foreach (CodeInstruction inst in instructions)
            {
                if (inst.opcode == OpCodes.Newobj)
                {
                    if (vec3ListCtor != null && getShadowVerts != null && Equals(inst.operand, vec3ListCtor))
                    {
                        yield return new CodeInstruction(OpCodes.Call, getShadowVerts).WithLabels(inst.labels);
                        continue;
                    }
                    if (intListCtor != null && getShadowIndices != null && Equals(inst.operand, intListCtor))
                    {
                        yield return new CodeInstruction(OpCodes.Call, getShadowIndices).WithLabels(inst.labels);
                        continue;
                    }
                    if (vec2ListCtor != null && Equals(inst.operand, vec2ListCtor))
                    {
                        vec2CtorOccurrence++;
                        MethodInfo targetHelper = (vec2CtorOccurrence == 1) ? getShadowUVs : getSkirt;
                        if (targetHelper != null)
                        {
                            yield return new CodeInstruction(OpCodes.Call, targetHelper).WithLabels(inst.labels);
                            continue;
                        }
                    }
                    if (floatListCtor != null && getSkirtW != null && Equals(inst.operand, floatListCtor))
                    {
                        yield return new CodeInstruction(OpCodes.Call, getSkirtW).WithLabels(inst.labels);
                        continue;
                    }
                }
                yield return inst;
            }
        }

        public static IEnumerable<CodeInstruction> AddOccludersFromCrewSimBlocks_Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            ConstructorInfo segListCtor = AccessTools.Constructor(typeof(List<Segment>), Type.EmptyTypes);
            MethodInfo getSegments = AccessTools.Method(typeof(VisibilityLightOptimizationModule), nameof(GetReusableOccluderSegments));

            foreach (CodeInstruction inst in instructions)
            {
                if (segListCtor != null && getSegments != null && inst.opcode == OpCodes.Newobj && Equals(inst.operand, segListCtor))
                {
                    yield return new CodeInstruction(OpCodes.Call, getSegments).WithLabels(inst.labels);
                    continue;
                }
                yield return inst;
            }
        }

        public static IEnumerable<CodeInstruction> IsCondOwnerLOSVisibleBlocks_Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            ConstructorInfo segListCtor = AccessTools.Constructor(typeof(List<Segment>), Type.EmptyTypes);
            MethodInfo getSegments = AccessTools.Method(typeof(VisibilityLightOptimizationModule), nameof(GetReusableLosSegments));

            foreach (CodeInstruction inst in instructions)
            {
                if (segListCtor != null && getSegments != null && inst.opcode == OpCodes.Newobj && Equals(inst.operand, segListCtor))
                {
                    yield return new CodeInstruction(OpCodes.Call, getSegments).WithLabels(inst.labels);
                    continue;
                }
                yield return inst;
            }
        }

        public static IEnumerable<CodeInstruction> EmitSkirt_Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            ConstructorInfo vec2ListCtor = AccessTools.Constructor(typeof(List<Vector2>), Type.EmptyTypes);
            MethodInfo getSkirtTemp = AccessTools.Method(typeof(VisibilityLightOptimizationModule), nameof(GetClearedSkirtTemp));

            foreach (CodeInstruction inst in instructions)
            {
                if (vec2ListCtor != null && getSkirtTemp != null && inst.opcode == OpCodes.Newobj && Equals(inst.operand, vec2ListCtor))
                {
                    yield return new CodeInstruction(OpCodes.Call, getSkirtTemp).WithLabels(inst.labels);
                    continue;
                }
                yield return inst;
            }
        }

        public static IEnumerable<CodeInstruction> LateUpdate_Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            List<CodeInstruction> list = new List<CodeInstruction>(instructions);

            MethodInfo vec3ToArray = AccessTools.Method(typeof(List<Vector3>), nameof(List<Vector3>.ToArray));
            MethodInfo setVerticesProp = AccessTools.PropertySetter(typeof(Mesh), nameof(Mesh.vertices));
            MethodInfo setVerticesFast = AccessTools.Method(typeof(VisibilityLightOptimizationModule), nameof(SetMeshVerticesFast));

            MethodInfo intToArray = AccessTools.Method(typeof(List<int>), nameof(List<int>.ToArray));
            MethodInfo setTrianglesProp = AccessTools.PropertySetter(typeof(Mesh), nameof(Mesh.triangles));
            MethodInfo setTrianglesFast = AccessTools.Method(typeof(VisibilityLightOptimizationModule), nameof(SetMeshTrianglesFast));

            MethodInfo vec2ToArray = AccessTools.Method(typeof(List<Vector2>), nameof(List<Vector2>.ToArray));
            MethodInfo setUvProp = AccessTools.PropertySetter(typeof(Mesh), nameof(Mesh.uv));
            MethodInfo setUVsFast = AccessTools.Method(typeof(VisibilityLightOptimizationModule), nameof(SetMeshUVsFast));

            for (int i = 0; i < list.Count - 1; i++)
            {
                if (vec3ToArray != null && setVerticesProp != null && setVerticesFast != null &&
                    list[i].Calls(vec3ToArray) && list[i + 1].Calls(setVerticesProp))
                {
                    list[i] = new CodeInstruction(OpCodes.Call, setVerticesFast).WithLabels(list[i].labels);
                    list[i + 1] = new CodeInstruction(OpCodes.Nop).WithLabels(list[i + 1].labels);
                }
                else if (intToArray != null && setTrianglesProp != null && setTrianglesFast != null &&
                         list[i].Calls(intToArray) && list[i + 1].Calls(setTrianglesProp))
                {
                    list[i] = new CodeInstruction(OpCodes.Call, setTrianglesFast).WithLabels(list[i].labels);
                    list[i + 1] = new CodeInstruction(OpCodes.Nop).WithLabels(list[i + 1].labels);
                }
                else if (vec2ToArray != null && setUvProp != null && setUVsFast != null &&
                         list[i].Calls(vec2ToArray) && list[i + 1].Calls(setUvProp))
                {
                    list[i] = new CodeInstruction(OpCodes.Call, setUVsFast).WithLabels(list[i].labels);
                    list[i + 1] = new CodeInstruction(OpCodes.Nop).WithLabels(list[i + 1].labels);
                }
            }

            return list;
        }
    }
}
