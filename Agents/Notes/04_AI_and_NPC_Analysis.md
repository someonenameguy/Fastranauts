# AI & NPC Management Analysis & Optimization Report (Subagent 4)

**Source:** Subagent 4 (AI & NPC Management Analyst)  
**Status:** Complete  

---

### Executive Summary

Analysis of decompiled game code across `AIShipManager.cs`, `AIShipQueue.cs`, `Ostranauts.Ships.AIPilots` (`AIShip`, `ScavPilot`, `BaseCharacter`, `FlyTo`, `FlyToPath`, `FlyToManual`), `CrimeManager.cs`, `BountyManager.cs`, and `PlotManager.cs` reveals key architectural bottlenecks causing high CPU overhead, garbage collection pressure, and frame hitches:

1. **Unthrottled Per-Frame AI Queue Execution (`AIShipManager.Update -> RunAIQueue`)**:
   - `RunAIQueue()` runs *every single frame* (`Update()`).
   - If the queue empties, it refills from all ships in `strATCLast` and `"INTERREGIONAL"`.
   - In each frame, `AIShipQueue.Dequeue()` returns **all priority ships in `_priorityQueue`** PLUS one ship from `_aIShipQueue`.
   - `list.Distinct()` is called every frame, generating per-frame LINQ and List allocations.
   - For all dequeued ships, `RunAI()` evaluates the full Behavior Tree every tick without distance LOD or rate limiting.

2. **Per-Frame Radar & Tracking Loop (`CheckTracking`)**:
   - Runs every frame in `AIShipManager.Update()`.
   - Iterates over all AI ships in the region (`GetShipsOfTypeForRegion(AIShipManager.strATCLast, AIType.All)`), allocating a new List each call.
   - Performs quadratic-like list lookups (`IndexOf`, `Add`, `Remove`) on strings (`aTrackCurrent`, `aTrackIgnores`) for every AI ship scanning player ship.

3. **High-Frequency Spawners and Distance Audits in `AIShipManager.Update()`**:
   - `SpawnFactionShips()`, `SpawnHauler()`, `SpawnPassShip()`, and `BountyManager.Update()` run on every frame.
   - `SpawnHauler()` performs full linear scans (`GetAllLoadedShipsWithin`) counting derelicts across multiple stations even when not strictly needed.
   - `SpawnPassShip()` iterates over all loaded ships, calls `ship.GetPeople(false).Count`, sorts a new list using `OrderByDescending`, and prunes/transfers NPCs across stations.

4. **Collision Course & Pathing Recalculations (`FlyToPath.PlanTrip4`, `AIShipManager.IsOnCollisionCourse`)**:
   - When an AI ship is executing `FlyTo`, it calls `AIShipManager.IsOnCollisionCourse()` which iterates across `CrewSim.system.GetAllLoadedShips()` calculating pairwise stopping distances and collision paths for non-docked ships.
   - `FlyToPath.PlanTrip4` is an iterative, recursive trajectory planner that causes major frame spikes when multiple ships calculate or fail paths repeatedly (triggering up to 5 consecutive rejected path retries).

5. **Linear Searches & LINQ Allocations in AI Queries**:
   - Frequent use of `dictAIs.Values` flattening, LINQ `.Where()`, `.Any()`, `.ToList()`, and `new Queue<AIShip>(...)` inside `UnregisterShip`.
   - `BountyManager.WatchForClaims()` and `MoveTargets()` iterate repeatedly over lists without spatial caching.

---

### Detailed Findings & Bottleneck Breakdown

#### 1. AIShipQueue & RunAIQueue CPU Overhead
*File: `AIShipManager.cs` (lines 2080-2104) & `AIShipQueue.cs` (lines 24-45, 89-93)*
- **Issue**:
  - `_priorityQueue` ships are evaluated *every frame*. Priority ships include Police, Navy, Pirates, or any ship within 20s of arrival (`base.ShipUs.objSS.NavData.GetArrivalEpoch() - StarSystem.fEpoch < 20.0`).
  - At 60+ FPS, running behavior trees with multi-node condition trees (`SelectorCommand`, `SequenceCommand`, sensor queries) on multiple ships produces continuous CPU churn.
  - `Dequeue()` constructs a `new List<AIShip>()` and calls `.Distinct<AIShip>()` every frame, causing constant heap garbage.
  - `UnregisterShip()` reallocates the queue and list with LINQ `new Queue<AIShip>(_aIShipQueue.Where(...))`.

#### 2. CheckTracking Frequency & Allocation
*File: `AIShipManager.cs` (lines 929-962)*
- **Issue**:
  - `GetShipsOfTypeForRegion(AIShipManager.strATCLast, AIType.All)` allocates a new `List<AIShip>` every frame.
  - String manipulation and list scanning: `ship.shipScanTarget.aTrackCurrent.IndexOf(ship.strRegID)` runs per AI ship.
  - This check is purely for the UI tracking warning (`selectedCrew.ship.trackWarning = flag`), which is completely unnoticeable if updated at 5-10 Hz instead of 60-144 Hz.

#### 3. Collision Course Detection (`IsOnCollisionCourse`)
*File: `AIShipManager.cs` (lines 2307-2356)*
- **Issue**:
  - Called inside `FlyTo.RunCommand()` on every tick of manual/path flight.
  - Iterates through `CrewSim.system.GetAllLoadedShips()`, constructing `Line` structs, running `CollisionManager.WillCollide()`.
  - For $N$ active flying ships and $M$ loaded ships, this is an $O(N \times M)$ geometric intersection test per AI update tick.
  - Denser asteroid belts or ship traffic clusters cause severe degradation.

#### 4. NPC Transits, Pruning, & Plot Checking
*File: `AIShipManager.cs` (`HandleTransits`, `HandleDeads`, `HandleRESTresspass`), `PlotManager.cs`, `CrimeManager.cs`*
- **Issue**:
  - `PlotManager.CheckPlots()` is called whenever `pRecheckPlotType != NONE`. It shuffles all plot names (`MathUtils.ShuffleList`) and parses condition triggers across protagonists.
  - `CrimeManager.EvaluateCrime()` iterates across all people on a ship, running `Visibility.IsCondOwnerLOSVisibleBlocks()` (raycasting/LOS checks) for every living bystander.

---

### Proposed Distance-Based Level-of-Detail (LOD) & Rate-Limiting Architecture

To eliminate CPU bottlenecks without breaking gameplay immersion:

#### Tiered Distance-Based LOD Model for Ship AI

Divide active AI ships into 3 LOD tiers based on distance $D$ to the player ship:

| LOD Tier | Distance to Player | Update Frequency | AI Processing Level |
|---|---|---|---|
| **LOD 0 (Near / Tactical)** | $D \le 150\text{ km}$ ($1.0\times 10^{-6}\text{ AU}$) or in combat / docking with player | **Every 1 - 2 frames** (Full fidelity) | Full Behavior Tree, collision avoidance, precise RCS docking/maneuvering. |
| **LOD 1 (Medium / Local ATC)** | $150\text{ km} < D \le 2,500\text{ km}$ | **Every 10 - 20 frames** (3 - 6 Hz) | Intermittent behavior updates, simplified collision checks, coarse trajectory tracking. |
| **LOD 2 (Distant / Regional)** | $D > 2,500\text{ km}$ or Interregional | **Every 60 - 120 frames** (0.5 - 1 Hz) | Direct vector interpolation / orbital integration, instant nav points, no per-frame collision checks. |

---

### Proposed Harmony Patches / Optimizations

#### Patch 1: Rate-Limit `CheckTracking()` to 0.2s Interval
```csharp
[HarmonyPatch(typeof(AIShipManager), "CheckTracking")]
public static class AIShipManager_CheckTracking_Patch
{
    private static float s_lastTrackingTime = 0f;
    private const float TRACKING_INTERVAL = 0.25f; // 4 Hz is more than enough for radar warnings

    public static bool Prefix()
    {
        if (Time.time - s_lastTrackingTime < TRACKING_INTERVAL)
        {
            return false; // Skip execution this frame
        }
        s_lastTrackingTime = Time.time;
        return true; // Run original method
    }
}
```

#### Patch 2: Zero-Allocation `AIShipQueue` & Tiered Processing
- In `AIShipQueue`:
  - Cache reusable list buffers instead of allocating `new List<AIShip>()` and `.Distinct()` every frame.
  - In `AIShipManager.RunAIQueue()`:
    - Instead of ticking all priority ships every single frame, tick max $K$ priority ships per frame (round-robin) unless they are in active combat ($D < 50\text{ km}$).
    - Distance filter: Ships farther than $2,500\text{ km}$ from the player only execute `RunAI()` once every 2 seconds.

#### Patch 3: Throttled Collision Prediction in `IsOnCollisionCourse`
- Throttle `AIShipManager.IsOnCollisionCourse` to run only every 0.5–1.0 seconds per ship rather than every tick, caching the `escapePoint`.
- Skip collision checking entirely for ships where $D > 500\text{ km}$ away from player or for ships moving at low relative speeds with no intersecting bounding spheres.

#### Patch 4: Optimization of `GetShipsOfTypeForRegion`
- Cache list results by ATC region ID and invalidate only on `AddAIToDict` or `UnregisterShip`, eliminating thousands of garbage allocations generated by `GetShipsOfTypeForRegion` calls in `Update()`, `CheckTracking()`, and `SpawnFactionShips()`.
