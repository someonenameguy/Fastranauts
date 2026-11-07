# Subagent 8: Memory, Garbage Collection & Allocations Analysis Report

**Target Scope:** Unity GC Pressure, Heap Allocations, Hot-Loop Garbage Generation in `CrewSim.cs`, `Ship.cs`, `CondOwner.cs`, `WorkManager.cs`, `GUIOrbitDraw.cs`, and related systems.  
**Objective:** Identify the root causes of GC collection spikes, stutter/frame-time jitter, and memory fragmentation, providing zero-allocation drop-in replacements, object pooling architectures, and non-allocating Harmony patches.

---

## 1. Executive Summary & GC Architecture in Unity Mono

Ostranauts operates on Unity's legacy Boehm-Demers-Weiser conservative, non-moving Garbage Collector (BDW-GC) / Incremental GC runtime:
1. **Non-Compacting Heap Dynamics:** Memory allocated on the managed heap cannot be relocated or compacted. When nursery or Gen0 thresholds are breached, a GC collection sweep is triggered. Intermittent large allocations result in severe heap fragmentation, continuously expanding the virtual memory working set and extending subsequent GC sweep pause durations.
2. **Stop-the-World Micro-Stutters:** Frequent Gen0 collections occur every few seconds when code allocates megabytes per second. Even small allocations (16–64 bytes) multiplied across thousands of simulation sub-steps or frame ticks trigger micro-stutters (pauses of 15ms–60ms), completely degrading gameplay responsiveness and frame consistency.
3. **Primary Culprits Identified:**
   - **Hot Loop Iteration Allocations:** Deep simulation ticks call methods like `CondOwner.Cleanup()`, `CondOwner.UpdateManual()`, `Ship.GetAllDockedShips()`, and `Ship.GetPeople()` which allocate new `List<T>`, `Dictionary<K,V>`, or call `.ToArray()` / `.ToList()`.
   - **Sub-stepping Multiplier:** `CrewSim.Update()` sub-steps the simulation up to hundreds of times per frame during high-speed time compression (4x–16x). An allocation of even 100 bytes per ticker per sub-step translates to hundreds of megabytes of garbage generated within seconds.
   - **Per-Frame UI & Physics Allocations:** `CrewSim.MouseHandler()` invokes `Physics.RaycastAll()` (allocating a new `RaycastHit[]` array every single frame), followed by `FindCOsAtWorldPosition()` allocating multiple `List<CondOwner>` instances. `CrewSim.DrawPower()` repeatedly constructs/adds thousands of points (`Vector2` structs resized into `List<Vector2>`).
   - **LINQ and Closure Allocations:** Critical AI methods like `CondOwner.GetNearestTriggered()` instantiate lambda closures (`OrderBy`, `Select`, `Where`) and tuples (`Tuple<double, CondOwner>`) inside target evaluation routines.
   - **Per-Frame String Allocations:** Concatenation in `CrewSim.Update()` debug overlays, AI key queries (`strID + interaction.strName`), and formatting calls creating temporary strings that immediately end up as garbage.

---

## 2. In-Depth Categorized Allocation Analysis

### Category A: Hot Loop Allocations in Simulation & Ticker Routines

#### A1. `CondOwner.Cleanup()`: The `dictRecentlyTried.Keys` Leak and GC Burn
- `Cleanup()` is called every 2 simulation seconds per `CondOwner`. Across 50–100 simulated characters/items with tickers, this runs constantly.
- Calling `new List<string>(this.dictRecentlyTried.Keys)` allocates an array and list header copying all string references on the heap.
- The `break;` statement terminates after removing at most **one** expired key. Meanwhile, AI interactions add keys into `dictRecentlyTried` at rates far exceeding 1 per 2 seconds. The dictionary expands to thousands of entries, generating tens of kilobytes per check and causing memory leak behavior.

#### A2. `CondOwner.UpdateManual()`: `aMUs` and `aMUDels` Dual-Pass Dispatch
- Three separate loop iterations over list buffers per ticker completion.
- List shifting on `aManUpdates.Remove(manUpdater3)` inside a loop causes multiple O(N) shifts.

#### A3. `CrewSim.UpdateICOs()`: Mass Temp List Duplication
- `CrewSim.aTickersTemp.AddRange(CrewSim.aTickers)` copies hundreds of element references from `UniqueList<CondOwner>` into `aTickersTemp` **every single sub-step** of `CrewSim.AdvanceSim()`.
- At 16x simulation speed, this single line copies 10,000–50,000 object references per frame.

---

### Category B: Query & Collection Allocations in `Ship.cs`

#### B1. `Ship.GetPeople()`: Pervasive List Heap Allocations
- Across the codebase, over 25 callers invoke `GetPeople()` on every tick. Each call allocates a brand new `List<CondOwner>()`. When `bAllowDocked` is true, it recursively calls `GetPeople(false)` on docked ships, creating nested temporary lists and concatenating via `AddRange()`.

#### B2. Array Cloning via `.ToArray()` on State Updates
- Every time an inventory change occurs (looting, moving, dropping items), the code allocates a `new List<T>`, copies the array, modifies it, and then invokes `.ToArray()` which allocates a second new array on the GC heap.

---

### Category C: Frame-Rate Per-Tick GC Churn (Update / LateUpdate)

#### C1. `CrewSim.MouseHandler()`: `Physics.RaycastAll` Heap Blast Every Frame
- In `CrewSim.Update()`, `this.MouseHandler()` runs **every single rendered frame**.
- Calls `Physics.RaycastAll(...)` which allocates a new `RaycastHit[]` array every single time it is called (60 to 144 times/sec).

#### C2. Struct-Based Sorting without Tuples or LINQ in `GetNearestTriggered`
- Candidates evaluated via `new Tuple<double, CondOwner>` and LINQ `.OrderBy().ToList().Select().ToList()` generates over 100 heap allocations per target query.

---

## 3. Concrete Non-Allocating Harmony Optimization Patches

### Patch 1: Zero-Allocation `Physics.RaycastNonAlloc` in `Ship.GetCOsAtWorldCoords1`
```csharp
[HarmonyPatch(typeof(Ship), "GetCOsAtWorldCoords1", new Type[] { 
    typeof(Vector2), typeof(CondTrigger), typeof(bool), typeof(bool), typeof(List<CondOwner>) 
})]
public static class Patch_Ship_GetCOsAtWorldCoords1_NonAlloc
{
    private static readonly RaycastHit[] _hitBuffer = new RaycastHit[256];

    [HarmonyPrefix]
    public static bool Prefix(Ship __instance, Vector2 vPos, CondTrigger ct, bool bAllowDocked, bool bAllowLocked, List<CondOwner> aOut)
    {
        if (aOut == null) return false;

        Ray ray = new Ray(new Vector3(vPos.x, vPos.y, -20f), Vector3.forward);
        int hitCount = Physics.RaycastNonAlloc(ray, _hitBuffer, 100f);

        Room roomAtWorldCoords = __instance.GetRoomAtWorldCoords1(vPos, bAllowDocked);
        CondOwner roomCO = roomAtWorldCoords != null ? roomAtWorldCoords.CO : null;
        CondOwner dockedRoomCO = null;

        for (int i = 0; i < hitCount; i++)
        {
            var hit = _hitBuffer[i];
            if (hit.transform == null) continue;

            CondOwner component = hit.transform.GetComponent<CondOwner>();
            if (component == null || component == roomCO || component.ship == null) continue;

            if (component.ship != __instance)
            {
                if (!bAllowDocked) continue;
                if (dockedRoomCO == null)
                {
                    Room r2 = component.ship.GetRoomAtWorldCoords1(vPos, false);
                    if (r2 != null && (ct == null || ct.Triggered(r2.CO, null, false)))
                    {
                        dockedRoomCO = r2.CO;
                        if (!aOut.Contains(dockedRoomCO)) aOut.Add(dockedRoomCO);
                    }
                }
            }
            else if (ct == null || ct.Triggered(component, null, false))
            {
                if (!aOut.Contains(component)) aOut.Add(component);
            }
        }

        if (roomCO != null && (ct == null || ct.Triggered(roomCO, null, false)))
        {
            if (!aOut.Contains(roomCO)) aOut.Add(roomCO);
        }

        return false;
    }
}
```

### Patch 2: Zero-Allocation Sorting in `CondOwner.GetNearestTriggered`
```csharp
public struct CODist : IComparable<CODist>
{
    public float DistSq;
    public CondOwner CO;
    public int CompareTo(CODist other) => DistSq.CompareTo(other.DistSq);
}

private static readonly List<CODist> _sortBuffer = new List<CODist>(128);

public static void SortInPlace(Vector2 vector, List<CondOwner> candidates)
{
    _sortBuffer.Clear();
    for (int i = 0; i < candidates.Count; i++)
    {
        var co = candidates[i];
        float d = (co.tf.position.ToVector2() - vector).sqrMagnitude;
        _sortBuffer.Add(new CODist { DistSq = d, CO = co });
    }

    _sortBuffer.Sort();

    candidates.Clear();
    int limit = Math.Min(10, _sortBuffer.Count);
    for (int i = 0; i < limit; i++)
    {
        candidates.Add(_sortBuffer[i].CO);
    }
    _sortBuffer.Clear();
}
```
