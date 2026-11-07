# Master Technical Architecture & Optimization Synthesis (Fastranauts)

## Executive Summary
This document integrates the findings of all 10 specialized analysis subagents into an actionable architecture and implementation roadmap for **Fastranauts**, a high-performance, non-breaking BepInEx optimization mod for *Ostranauts* (v1.0.0.9+).

The analysis identified key bottlenecks that degrade performance in late-game saves, crowded stations (OKLG), and during high-speed time compression (4x–16x). Every proposed patch has been cross-checked against reference mod patterns (such as *Take Their Ships*) and official BepInEx/Harmony modding guidelines to ensure compatibility with other mods.

---

## Synthesis of Subsystem Analyses

### 1. Game Loop & Sub-Stepping Simulation (`01_GameLoop_and_Ticker_Analysis.md`)
- **Root Cause**: `CrewSim.Update()` uses `aTickers.FirstOrDefault().fNextTickerSecs` to calculate sub-step size `num3` over an unsorted `UniqueList<CondOwner>`. Under high time compression (4x–16x), this forces hundreds of micro-steps per frame, repeatedly running `UpdateICOs()` and allocating garbage.
- **Memory Leak Bug**: `CondOwner.Cleanup()` allocates `new List<string>(dictRecentlyTried.Keys)` every 2 seconds, but contains a `break;` that only deletes one entry per cycle, leading to an unbounded memory leak.
- **Optimization**:
  - Clamp simulation sub-steps to a maximum of 16 per frame with a 16ms minimum step.
  - Implement zero-allocation in-place `IManUpdater` iteration and fix the `dictRecentlyTried` leak with a static key removal buffer.

### 2. Atmosphere & Gas Simulation (`02_Atmosphere_and_Gas_Analysis.md`)
- **Root Cause**: `GasContainer.Update()` runs every frame on all air volumes even when pressure and gas moles are static. Respiration (`GasPump.Respire2`) executes `Run(false)` up to 6 times per breath.
- **Optimization**:
  - Dirty-flag guard in `GasContainer.Update()`: early-exit when `!GasChanged && fDGasTemp == 0.0`.
  - Batch gas container updates in respiration so `Run()` is invoked once per breath cycle.
  - Fix the `GasExchange.MergeKeys` global void dictionary mutation bug.

### 3. Pathfinding & Grid Navigation (`03_Pathfinding_and_Navigation_Analysis.md`)
- **Root Cause**: `JumpPointSearch.GetPath()` allocates a new 2D nullable array `new bool?[X, Y]` (10KB–40KB) on every single query. `Vector2Int` lacks `IEquatable` / `GetHashCode` overrides, causing boxing and dictionary lookup overhead. `PathMemory` cache was effectively single-frame.
- **Optimization**:
  - Reusable generation-stamped grid buffer (`FastGridBuffer`) eliminating the 2D array allocation.
  - Fast `IEqualityComparer<Vector2Int>` to eliminate boxing.
  - Time-to-live path caching (0.5s–1.0s) avoiding redundant route re-evaluations.

### 4. AI & NPC Management (`04_AI_and_NPC_Analysis.md`)
- **Root Cause**: `AIShipManager.Update()` executes `RunAIQueue()` and `CheckTracking()` every frame. Priority ships run full behavior trees every frame regardless of distance. `IsOnCollisionCourse` performs $O(N \times M)$ pairwise checks per tick.
- **Optimization**:
  - Tiered Distance-based Level-of-Detail (LOD):
    - LOD 0 (Tactical $\le$ 150 km): Full fidelity.
    - LOD 1 (Local 150–2,500 km): Throttled updates (3–6 Hz).
    - LOD 2 (Regional > 2,500 km): Vector interpolation (0.5–1.0 Hz).
  - Rate-limit radar tracking checks to 4 Hz (0.25s interval).

### 5. DataHandler, Conditions & Reflection (`05_DataHandler_and_Conditions_Analysis.md`)
- **Root Cause**: Hot path lookups (`GetLoot`, `GetItemDef`, `GetInteraction`) perform redundant `.ContainsKey()` followed by `dict[]`. `GetCondAmount(..., isThreshold: true)` allocates substrings on every call. `GetCond` instantiates new `Condition` wrapper objects solely for metadata reading.
- **Optimization**:
  - Replace `ContainsKey` + indexer with single `TryGetValue`.
  - String pool cache (`ThresholdStringPool`) for `Thresh` prefix stripping.
  - Direct non-allocating condition definition accessor (`GetCondDef`).
  - Cache `PropertyInfo` reflections in `DataHandler.JsonToData`.

### 6. Electrical & Power Grid (`06_Electrical_and_Power_Analysis.md`)
- **Root Cause**: `TileUtils.GetPoweredTiles()` performs $O(N^2)$ BFS queue scanning (`IndexOf`) and $O(M^2)$ edge duplication checking across all ship tiles. `Electrical.cs` string-concatenates signal queues on every toggle. `DrawPower()` transforms up to 7,600 coordinates from world-to-screen every frame.
- **Optimization**:
  - Overhaul BFS queue with `HashSet<Tile>` and array queue.
  - Disable `Electrical.Update()` when queues are empty; decouple in-memory signal handling from UI string serialization.
  - Replace screen-space power line projection with world-space meshes and UV scrolling shaders.

### 7. UI, Canvas & Layout Rendering (`07_UI_and_Rendering_Analysis.md`)
- **Root Cause**: Ground inventory (`GUIInventoryWindow.UpdateWindow`) calls `Redraw()` every single frame, raycasting and checking line-of-sight across a 25-tile grid at 60–144 Hz. `ForceRebuildLayoutImmediate` is triggered twice consecutively across UI modules.
- **Optimization**:
  - Throttle ground inventory redraws to 4 Hz (0.25s) with a player displacement dirty-check (`sqrMagnitude > 0.0625f`).
  - Replace synchronous layout rebuilds with deferred `MarkLayoutForRebuild`.
  - Disable `GraphicRaycaster` components during canvas alpha fades.

### 8. Memory, Garbage Collection & Allocations (`08_Memory_and_GC_Analysis.md`)
- **Root Cause**: Frequent Gen0 stop-the-world GC pauses caused by `Physics.RaycastAll` in `CrewSim.MouseHandler` (allocating new `RaycastHit[]` every frame), LINQ `.OrderBy().ToList()` and `Tuple` allocations in `CondOwner.GetNearestTriggered()`, and pervasive `Ship.GetPeople()` allocations.
- **Optimization**:
  - Replace `Physics.RaycastAll` with `Physics.RaycastNonAlloc` using static buffers.
  - Replace LINQ and `Tuple` candidate sorting with an in-place struct distance buffer.
  - Implement `GetPeopleNonAlloc` reusable collection query.

### 9. Physics, Collision & Orbit Mechanics (`09_Physics_Collision_and_Orbit_Analysis.md`)
- **Root Cause**: Space collision detection runs unpartitioned $O(N^2)$ distance comparisons over all ships in the system. Projectile checks allocate `.ToList()` and solve quadratic trajectories every tick. Numerical differentiation in `BodyOrbit.UpdateTime` recursively invokes Kepler solvers twice per body.
- **Optimization**:
  - Spatial hash grid (0.001 AU coarse cells) reducing collision checking to $O(N)$.
  - Pre-cached docked ship ID hashsets eliminating LINQ `.Select().ToList()` in inner loops.
  - Analytical orbital velocity calculation and timestamp guards preventing redundant Kepler iterations.

### 10. Architecture & Mod Compatibility (`10_Architecture_and_Mod_Compatibility_Design.md`)
- **Design Structure**:
  - Support dual deployment: Steam Workshop (via `OstranautsWorkshopBepInExBridge`) and manual BepInEx installation.
  - Strict non-destructive patching principles: use throttling prefixes and post-filtering rather than unconditional overrides.
  - Full compatibility verified with reference mods (e.g. *Take Their Ships*).
  - Modular architecture with individual config switches, live diagnostics (`fastinfo`), and exception fallbacks.

### 11. Runtime Profiler Screenshot Analysis (`11_Profiler_Screenshot_Analysis.md`)
- **Top Empirical Bottlenecks Captured in Live Gameplay**:
  1. **`PDAVisualisers::Update` (`24.20 ms` | `211,306 B` Mem)**: Every 0.5s during live overlay presets (Power, Heat, Pressure, O2, Atmo, etc.), `NotifyShips()` calls `ship.VisualizeOverlays(true)`. Passing `force = true` bypasses the dirty check in `Item.VisualizeOverlays` (`Item.cs:169`), forcing `GetPropertyBlock`/`SetPropertyBlock` across thousands of renderers, while `Ship.VisualizeOverlays` clones `"Blank"` `CondTrigger` and allocates massive `List<CondOwner>` copies via `GetCOs`.
  2. **`CrewSim::Update` (`3.80 ms` | `22,072 B` Mem)**: Per-frame `Physics.RaycastAll` in `MouseHandler()`, `aTickersTemp.AddRange` copies, and `CondOwner.Cleanup()` dictionary key list allocations.
  3. **`GasPressureSense::Update` (`0.21 ms` | `2,219 B` Mem per door)**: Every airlock/door sensor allocates `new List<CondOwner>()`, clones `DataHandler.GetCondTrigger(strCTClear)` (`bClone = true` by default), and runs `Physics.RaycastAll` in `GetCOsAtWorldCoords1`.
  4. **`Visibility::LateUpdate` (`0.06 ms` | `715–933 B` Mem per work lamp)**: `ResetLightMesh()` and `AddOccludersFromCrewSimBlocks()` instantiate 6 `new List<T>()` objects every frame, and `LateUpdate()` calls `.ToArray()` 3 times (`shadowVerts`, `shadowIndices`, `shadowUVs`) instead of `Mesh.SetVertices(List<Vector3>)`.
  5. **`GUIItemList::Update` (`0.06 ms` | `144 B` Mem)**: Rebuilds hover item and pressure tooltip strings every frame via `string.Concat` and `.ToString("n2")`.

---

## Master Implementation Roadmap

| Priority | Module | Key Patches | Projected Gain |
|---|---|---|---|
| **P0** | **PDA Overlays & Shaders** (`11_Profiler`) | Pass `force = false` on periodic `_pingTime` updates in `PDAVisualisers`; zero-alloc `Ship.VisualizeOverlays` without `GetCondTrigger("Blank")` | Eliminates **24.20 ms** periodic frame spikes and **211 KB** GC allocations |
| **P0** | **Game Loop, Doors & Lamps** (`01`, `08`, `11`) | `Physics.RaycastNonAlloc`, `GasPressureSense` non-cloning trigger & list reuse, `Visibility` list reuse + `Mesh.SetVertices`, `dictRecentlyTried` leak fix | Eliminates **22 KB/frame** in `CrewSim`, **2.2 KB/door** in `GasPressureSense`, and **~850 B/lamp** in `Visibility` |
| **P0** | **UI & Inventory** (`07`, `11`) | Ground inventory 4 Hz throttle + position dirty-check, `GUIItemList` 10 Hz throttle, deferred layout rebuilds | +40 to 60 FPS while inventory is open, removes tooltip hover hitching |
| **P1** | **Pathfinding & Grid** (`03`) | JPS zero-allocation grid buffer, `Vector2Int` fast comparer, path TTL caching | Eliminates frame drops during crew movement & boarding |
| **P1** | **AI & Radar** (`04`) | Tiered distance LOD (Tactical/Local/Regional), 4 Hz radar tracking rate limit | Smooths FPS in crowded station regions (OKLG) |
| **P2** | **Gas & Electrical** (`02`, `06`) | GasContainer idle early exit, BFS power grid overhaul, empty electrical queue sleep | Stabilizes simulation tick time on large/multi-docked ships |
| **P2** | **Physics & Orbit** (`09`) | Spatial hash broadphase, docked ID sets, analytical orbit velocity | Scales gracefully to 50+ ships in the star system |
