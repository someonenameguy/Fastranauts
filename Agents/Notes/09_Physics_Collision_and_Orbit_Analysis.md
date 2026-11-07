# Ship Collision, Physics & Orbit Analysis & Optimization Report (Subagent 9)

**Source:** Subagent 9 (Ship Collision, Physics & Orbit Analyst)  
**Status:** Complete  
**Scope Analyzed:** 
- `CollisionManager.cs`
- `Ship.cs` (physics, docking, collision detection, movement)
- `BodyOrbit.cs` & `StarSystem.cs` (orbital propagation, celestial body updates, gravity hierarchy)
- `Room.cs`, `Block.cs`, `Ostranauts.Ships.DamageSystem.cs`, `GridUtils.cs`, `ShipQueue.cs`

---

## 1. Executive Summary

In *Ostranauts*, space physics, orbital propagation, and ship collisions operate on astronomical scales (AU coordinates, double precision) alongside discrete grid-based interior combat and damage raycasting. 

Our deep-dive inspection of the decompiled code identifies several severe architectural bottlenecks:

1. **Broadphase Collision Detection Degradation ($O(N^2)$ Pairwise Checks)**:
   - `CollisionManager.CheckCollisions()` and `RunQueue()` are driven every frame from `StarSystem.Update()`:
     - The player ship and all docked ships check against every ship in `CrewSim.system.dictShips.Values` synchronously.
     - `CollisionManager.RunQueue()` processes queued loaded ships, each executing a full loop over `CrewSim.system.dictShips.Values` and `CrewSim.system.aBOs`.
     - Broadphase distance checking uses Euclidean distances in AU with no spatial partitioning (no BVH, spatial hashing, or grid indexing).
     - Inside the inner loop over all ships, LINQ queries (`from x in shipCheck.GetAllDockedShips(null) select x.strRegID`), dictionary lookups, and distance calculations execute hundreds of times per tick.

2. **Projectile Collision Bottleneck**:
   - `CollisionManager.CheckProjectileCollisions(double timeDelta)` is executed on every `StarSystem.Update()`.
   - Every tick, `CrewSim.system.dictShips.Values.ToList<Ship>()` and `CrewSim.system.dictProjectiles.Values.ToList<Ship>()` allocate fresh `List<Ship>` collections on the heap.
   - For every projectile, it iterates over all ships ($M \times N$), computes relative quadratic trajectory intersection via `CollisionManager.WillCollide()` solving a quadratic root equation, and performs shallow damage raycasting on impact.

3. **BodyOrbit Time Updating & Redundant Matrix/Trigonometric Solvers**:
   - `StarSystem.Update()` iterates through `this.aBOs.Values` and calls `bodyOrbit.UpdateTime(StarSystem.fEpoch, true, true)`.
   - Then, inside `CollisionManager.CheckCollisions(shipCheck)`, it iterates **again** through all `CrewSim.system.aBOs` and calls `keyValuePair.Value.UpdateTime(StarSystem.fEpoch, true, true)`.
   - If velocity calculation (`bCalcV = true`) is triggered, `UpdateTime` recursively invokes itself for $t - 1.0$ and $t$, causing repeated recursive Kepler solver iterations.
   - `SolveBigE` runs iterative Newton-Raphson (up to 10 iterations) evaluating transcendental functions (`Math.Sin`, `Math.Cos`, `Math.Floor`) for every celestial body and moon.
   - Furthermore, `GetGreatestGravBOFast()` executes for every ship and every docked anchor on every tick, traversing the body hierarchy and calculating Newton gravitational accelerations.

4. **Docking Group Multi-Body Synchronization Overhead**:
   - Docking is represented by `DockGroup` hierarchies. When ships are docked, `UpdateDockingGroups` advances time on the anchor ship, but `CollisionManager.ProcessCollision` repeatedly traverses `GetAllDockedShips(null)`, recalculates total mass by iterating child ships, sets `objSS.vPos` and `objSS.vVel`, and updates positions/velocities in nested loops with LINQ calls.

5. **Tile and Interior Raycasting in DamageSystem & Room Creation**:
   - `DamageSystem.DamageRayShallow()` creates a full 2D array grid (`GridUtils.CreateShallowItemGrid`) every time a collision occurs or projectile hits, allocating `List<DataCOWrapper>[,]` of grid dimensions and scanning all `ship.GetMappedCos()`.
   - `DamageRayRandom()` / `DamageRay()` performs `Physics.RaycastAll()` in Unity 3D space followed by LINQ sorting: `array.OrderBy<RaycastHit, float>(go => go.distance).ToArray()`, producing heavy GC allocations on every collision tick.
   - Room generation (`Ship.CreateRooms()`) performs full tile iteration across thousands of ship tiles, running LINQ queries (`FirstOrDefault<Room>`), building multiple dictionaries and hashsets, and generating redundant string concatenations.

---

## 2. Architectural Optimization Proposals & Harmony Patches

### 2.1 Spatial Hash Grid Broadphase for Space Collisions
Instead of scanning all `CrewSim.system.dictShips.Values` for each ship:
- Partition the system into coarse grid cells (e.g. 0.001 AU $\approx$ 150,000 km per cell).
- Ships only query their own cell and immediate adjacent neighbor cells (9 cells total in 2D).
- Reduces collision broadphase complexity from $O(N^2)$ to $O(N)$.

### 2.2 Elimination of LINQ & Cache `DockedShipIDs`
- Add a cached `HashSet<string> DockedRegIDs` directly on `ShipSitu` / `DockGroup`.
- Whenever a ship docks or undocks, update the hash set once.
- In `CheckCollisions`, replace linear string search with $O(1)$ set lookup.
- Zero garbage allocation in `CheckCollisions`.

### 2.3 Analytical Orbit Velocity & Timestamp Guard
In `BodyOrbit.cs`:
- Move timestamp check to the very top:
  `if (Math.Abs(this.dTimeCalcLast - dTime) < 1E-6) return;`
- Replace numerical differentiation ($t$ and $t - 1.0$) with analytical velocity derivatives to eliminate recursive Kepler iterations.

### 2.4 Non-Allocating Damage Raycasting
In `DamageSystem.DamageRay()`:
- Replace `Physics.RaycastAll` and LINQ `OrderBy` with `Physics.RaycastNonAlloc` and `Array.Sort` over static reusable buffers.
- Pool and reuse a single `DataCOWrapperGridBuffer` in `DamageRayShallow`.

### 2.5 Accelerated `CreateRooms` Algorithm
In `Ship.CreateRooms()`:
- Replace linear LINQ `FirstOrDefault` with temporary dictionary indexing.
- Cache wall/portal properties as bitmask flags on `Tile` to avoid condition dictionary lookups during room flood-fills.
