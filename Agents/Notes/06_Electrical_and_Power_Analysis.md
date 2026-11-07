# Subagent 6 Report: Electrical & Power Grid Analysis

## 1. Executive Summary
This report analyzes Ostranauts' electrical control and bulk power distribution systems based on decompiled source code (`Electrical.cs`, `Powered.cs`, `TileUtils.cs`, `Ship.cs`, `GUIPowerOverlay.cs`, `CrewSim.cs`, `PowerUsageRecorder.cs`).

The game utilizes two distinct network systems:
1. **Low-Voltage / Digital Control Grid (`Electrical.cs`)**: A logical gating and signal propagation network between switches, sensors, delays, and appliances using `ElectricalSignal` and `ElectricalConnection`.
2. **High-Voltage / Bulk Power Distribution Grid (`Powered.cs` & `TileUtils.GetPoweredTiles`)**: A physical tile-conductive power mesh that connects power sources (`IsPowerGen`), storage (`IsPowerStorage`), recharging containers, and consumers (`bUsesPower`).

Our analysis identified critical architectural and algorithmic bottlenecks:
- **Redundant Dual Update Loops**: `Electrical.cs` runs `Update()` via Unity engine message and is called redundantly by `CondOwner.EndTurn()` via `IManUpdater.UpdateManual()`.
- **String Serialization Overhead in Signal Routing**: Every queued, processed, or toggled signal serializes all connections and queues into comma/hash-separated strings (`SetGPM()`) stored in `mapGUIPropMaps`, thrashing string memory and generating massive garbage collection pressure.
- **Unbounded Tree Traversal & Recursion**: `Powered.TransmitPower()` recursively traverses input points down adjacent tiles with recursive fallback searches and quadratic unmarking scans (`UnmarkTransmits()`).
- **Quadratic Grid Flood Fill**: `TileUtils.GetPoweredTiles()` conducts quadratic BFS searches across every tile on a ship repeatedly using `list.IndexOf()` and linear scans of `objShip.aPwrTiles` (`Count - 2`), without cached adjacency or spatial partitioning.
- **Frame-by-Frame GUI Overlay Thrashing**: When the Power Visualizer overlay is active (`CrewSim.PowerVizVisible`), `CrewSim.Update()` iterates every power line vector, re-projecting world coordinates via `camMain.WorldToScreenPoint()` every frame for up to 7,600 tile points, while `GUIPowerOverlay` repeatedly queries conditions and instantiates materials.

---

## 2. Electrical Simulation Frequency & Architecture

### 2.1 Dual-Update Redundancy in `Electrical.cs`
`Electrical` inherits from `MonoBehaviour` and implements `IManUpdater`:
- Unity engine calls `Electrical.Update()` once per frame. Additionally, `CondOwner.UpdateTickers()` -> `EndTurn()` iterates `this.aManUpdates` and executes `manUpdater2.UpdateManual()`. Thus, `Electrical.Update()` is invoked twice per simulation frame for active electrical entities.
- In `Update()`, `this.fTimeToNextAudit` decrements by `Time.unscaledDeltaTime`. Calling it twice per frame causes audit intervals to elapse at double the intended speed.
- Every active electrical component polls every frame even when `this.bNeedsCheck` is `false`.

### 2.2 Simulation Frequency in `Powered.cs`
Bulk power simulation runs inside `Powered.Update()`:
- **Major Power Cycle (`Run()`)**: Runs once every 1.0 epoch seconds per `Powered` instance with random start offsets, causing non-deterministic power competition between appliances.
- **Flicker Logic**: Runs every frame on every light item, computing random floats and quadratic damage formulas.
- **Minor Power Check**: Ticks every 0.3 epoch seconds, traversing root parents up to 10 hierarchy levels.

---

## 3. Graph Traversal & Network Solving Costs

### 3.1 Bulk Power Grid Generation (`TileUtils.GetPoweredTiles`)
- **O(N^2) BFS Queue Check**: `list.IndexOf(tile3)` scans `list` linearly on every adjacent tile step.
- **O(M^2) Edge Duplication Scan**: `objShip.aPwrTiles` is checked via linear scan (`for int k = 0; k < objShip.aPwrTiles.Count - 2; k += 2`).
- **Repeated Passes per Power Source**: If a ship has 10 batteries and 2 reactors, the entire power grid is flood-filled 12 distinct times, clearing and resetting `bPathChecked` on every single tile between each iteration.

### 3.2 Deep Recursion in `Powered.TransmitPower()`
- If an intermediate storage device runs out of power, `TransmitPower()` traverses connected points, then `UnmarkTransmits()` recursively visits all connected points again simply to clear `bNoPowerThisLoop`.

### 3.3 String Serialization in `Electrical.cs` (`mapGPM` Thrashing)
- `AddSignal()`, `QueueSignal()`, and `SetGPM()` repeatedly concatenate strings (`+ "," + conn.ToString()`) and write to `mapGUIPropMaps`, generating tens of kilobytes per frame during switching operations.

---

## 4. Power Overlay Rendering Costs (`CrewSim` & `GUIPowerOverlay`)

- In `CrewSim.Update()`, `DrawPower()` transforms up to 7,600 tile coordinates from 3D world space to 2D screen space every frame using `Camera.WorldToScreenPoint()`.
- `VectorLine.Draw()` rebuilds dynamic vertex buffers every frame on the main UI thread even when the ship and camera are completely static.

---

## 5. Concrete Optimization Proposals & Harmony Patches

### 5.1 Topology Caching & BFS Overhaul in `TileUtils.GetPoweredTiles`
- Replace `List<Tile>.IndexOf` with a `HashSet<Tile>` and array-based BFS queue.
- Use Disjoint-Set (Union-Find) or component labeling when walls/floors are placed or destroyed, rather than reflooding for every battery/generator.

### 5.2 Event-Driven Electrical Signal Architecture
- In `Electrical.Update()`, if `signalQueue.Count == 0` and `sendQueue.Count == 0`, disable the component (`this.enabled = false`). Enable on `AddSignal()` or `QueueSignal()`.
- Stop string serialization to `mapGPM` on every signal tick; keep signals in memory and serialize only on save or UI open.

### 5.3 Eliminate Recursive Transmit & Traversal ID Stamp
- Replace recursive `UnmarkTransmits()` with a monotonic traversal generation counter (`uint nLoopID`). If `pwr.nLastLoopID == currentLoopID`, skip to prevent cycles without a second cleanup traversal.

### 5.4 Screen-Space Power Line Caching & World-Space Mesh
- Render power lines as a world-space mesh child of the Ship transform with a UV-scrolling shader, offloading coordinate transformation and animation to the GPU.

---

## 6. Implementation Priority Matrix

| Optimization | Expected Gain | Implementation Complexity | Risk Level |
|---|---|---|---|
| Disable `Electrical.Update()` when queues are empty | High CPU reduction on large ships | Low | Minimal |
| Stop `mapGPM` string serialization on every signal tick | Eliminates huge GC pressure during battles/switches | Medium | Low |
| Replace `TileUtils.GetPoweredTiles()` O(N^2) scans with HashSet/Queue | Massive reduction in ship edit / tile update hitches | Medium | Low |
| Eliminate LINQ `.OrderBy()` / `Distinct()` in `GatherPower()` | Reduces per-second GC garbage from powered devices | Low | Minimal |
| Move `CrewSim.DrawPower()` to World-Space Mesh / Shader animation | Drastic FPS recovery when PDA Power Visualizer is active | Medium | Minimal |
| Eliminate recursive `UnmarkTransmits()` via traversal stamp ID | Eliminates stack overhead & double traversal | Low | Minimal |
