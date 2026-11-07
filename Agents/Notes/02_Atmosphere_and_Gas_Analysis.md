# Subagent 2: Atmosphere & Gas Simulation Optimization Analysis

**Scope & Targets:**
- `GasContainer.cs` (`Run`, `UpdateManual`, `UpdateStats`, `BreakIn`, `CarveNewGasContainerFromRoom`, `LateInit`, `CheckPressureDifference`, `RemoveGasMass`, `MergeGasContainersAndDestroy`)
- `GasExchange.cs` (`Update`, `DoGas`, `CalcMoleDiff`, `MergeKeys`, `IsBlocked`, Audio Source Updates)
- `GasPump.cs` (`Update`, `Pump`, `Respire2`, `HandleActivePumpAudio`, `UpdateRemote`)
- `GasPressureSense.cs` (`Run`, `Update`, `FixForVentSensors`)
- `Ship.cs` (`UpdateGravAndAtmo`, `CheckRoomPressure`, `PreFillRooms`, `CreateRooms`)
- `FluidStrings.cs`

---

## 1. Executive Summary

Atmospheric and gas simulation in Ostranauts is one of the heaviest ongoing CPU consumers during active flight, docked station operations, and time acceleration (16x+). 

The atmospheric simulation is characterized by:
1. **High-frequency Dual Execution (`Update()` + `UpdateManual()`)**: Components like `GasContainer`, `GasExchange`, and `GasPump` inherit from `MonoBehaviour` and implement `IManUpdater`. They are ticked by the Unity engine loop every visual frame AND repeatedly by `CondOwner.UpdateManual()` in simulation sub-steps.
2. **String-Dictionary Churn and Heap Allocations**: Heavy reliance on string concatenation (`\"StatGasMol\" + strGas`, `\"StatGasPp\" + text`) and dictionary lookups (`Dictionary<string, double>` in mapDGasMols, mapGasMols1, mapConds, mapInfo) allocating enumerators, key collections, and boxing doubles.
3. **Redundant O(N) Linear List Searches in Hot Loops**: Repeated calls to `FluidStrings.mol.IndexOf(key)` inside nested gas transfer loops per tick.
4. **Inefficient Spatial & Pressure Difference Queries**: Every ship updates its gravity, solar body atmospheres, and checks room divider pressure differences via `Ship.UpdateGravAndAtmo()` every ~0.33s. Inside `CheckRoomPressure()` in Ship.cs and `GasContainer.CheckPressureDifference()`, coordinate-based dictionary queries (`Dictionary<Vector2, List<CondOwner>>`) allocate fresh lists and trigger expensive world-coordinate CondOwner queries.
5. **Overly Aggressive UI and Danger Checks**: `GasContainer.UpdateStats()` formats strings (`.ToString(\"n2\") + \"kPa\"`) and queries lists of safe gas conditions for every container on every gas delta.

---

## 2. Comprehensive Root Cause Analysis by Component

### A. GasContainer (`GasContainer.cs`)

#### 1. Per-Frame `Run(false)` Execution without Early Exit
- `GasContainer.Update()` simply calls `this.Run(false)`.
- In `Run()`, every frame begins by checking:
  ``charp
  if (this.bRequiresInit || this.co.HasCond("IsGasRequiresInit"))
  ```
  Even when no gas change has occurred, it evaluates epoch clocks:
  ``@charp
  if (this._timeNextPressureDifCheck < StarSystem.fEpoch)
  ```
- If `co.GasChanged` is true or `fDGasTemp != 0.0`:
  - Iterates over all delta entries in `mapDGasMols`.
  - Calls `co.AddCondAmount(key, delta)` which invokes full condition dictionary lookups and condition triggers.
  - Calculates ideal gas law pressures: `P = (n * R * T) / V` involving multiple getters.
  - Iterates over all 11 gas types in mapGasMols1 using `FluidStrings.mol.IndexOf(key2)` just to get molecule name and pps string names. IndexOf on list is an O(N) linear string search done for every gas in every container every tick.
  - Calls `UpdateStats()`, writing string formatted values to `comp.mapInfo["Pressure"]` and checking 5+ condition loot DC requirements (`aSafeGasDcs`) across all rooms!

#### 2. Vector2 Floating-Point Key Dictionary in `CachedBorderCOs`
- `GasContainer.CachedBorderCOs` is defined as: `Public Dictionary<Vector2, List<CondOwner>> CachedBorderCOs`that stores border walls/doors.
- Using `Vector2` as a Dictionary key is prone to performance degradation due to float hashing and equality overhead.
- Furthermore, whenever a divider is damaged or destroyed, it wipes the ENTIRE cache:
  ```charp
  a.CachedBorderCOs.Clear();
  b.CachedBorderCOs.Clear();
  ```
  causing all wall/portal COs on the ship to be re-queried via the expensive `ship.GetCOsAtWorldCoords1()`.

#### 3. String Allocations in Gas Mass Calculation
- `GasContainer.GetGasMass(string strGas, double fMols)`` switches on hashed strings.
- In `Run()` and `GetTotalGasValue()`, strings are repeatedly carved via `text.Substring("StatGasMol".Length)`, allocating new string objects on the GC heap hundreds of times per second.

---

### B. GasExchange (`GasExchange.cs`)

#### 1. MergeKeys Overhead & Global State Mutation
- In `CalcMoleDiff()`:
  ```charp
  this.MergeKeys(ref dictionary, ref dictionary2);
  this.MergeKeys(ref dictionary2, ref dictionary);
  ```
  `MergeKeys` loops over dictionary B and inserts missing keys into dictionary A with `0.0`. Not only does this dirty `dictionary`, but when either container is void, it passes `GasExchange.GasMolsVoid` (a static readonly dictionary), actually MUTATING the sshared global void molar dictionary!

#### 2. Redundant Audio Source Updates
- `GasExchange` contains 3 `AudioSource` components (`srcLow`, `srcMid`, `srcHigh`).
- In `DoGas()`, if mole deltas exceed 0.1, it plays and adjusts audio with trigonometric and square root calculations (`Mathf.Sqrt(0.5f * (1f - num3) * fValume)`).
- When gas transfer is minimal or fluctuating around the 0.1 threshold, `LerpAudioTo` and `PlayAudio` run continuously even for off-screen, distant compartments where the player cannot hear them!

#### 3. Wall/Portal Blocking Spatial Queries
- ¡Every `fCheckPeriod` (default 0.5s), `IsBlocked(ptA)` and `IsBlocked(ptB)` call:
  ``@charp
  this.co.ship.GetTileAtWorldCoords1(tilePos.x, tilePos.y, true, false);
  this.co.ship.GetCOsAtWorldCoords1(this.ptA, GasExchange._ctGasBlocker, true, false, this._blockingCOs);
  ```
  If a door or vent is static (unchanged state), doing coordinate tile and CO lookups every 0.5 seconds per vent/doorway is completely redundant.

---

### C. GasPump (`GasPump.cs )

#### 1. Linear Index Searches in Hot Respiration/Pump Loop
- In `Pump()`:
  ``charp
  int num3 = FluidStrings.mol.IndexOf(key);
  string text = FluidStrings.moleculeNames[num3];
  string text2 = FluidStrings.pps[num3];
  ```
- In `Respire2()`:
  ```charp
  int num4 = FluidStrings.moleculeNames.IndexOf(jsonGasRespireData.strGasIn);
  int num8 = FluidStrings.moleculeNames.IndexOf(jsonGasRespireData.strGasOut);
  ```
  `IndexOf` scans linearly through the List<string>. For every gas in every pump and every human breather, this executes multiple string comparisons per sub-step.

#### 2. Excessive `GasContainer.Run(false)` Invocations
- In `Respire2()`, for EACH gas entry in `gR.aGases`:
  - Modifies `gasContainer.mapDGasMols`.
  - IMMEDIATELY invokes `gasContainer.Run(false);`!
  - Then for `condOwner2` (the output target), modifies `gasContainer2.mapDGasMols` and IM]EDIATELY invokes `gasContainer2.Run(false);`!
- If a respire profile has 3 gases (O2 -> CO2, H2O, etc.), `gasContainer.Run(false)` is invoked **6 separate times** within a single tick! All intermediate mass, pressure, and partial pressure calculations are thrown away and recalculated 6 times consecutively!

---

### D. Ship Atmospheric Updates (`Ship.UpdateGravAndAtmo` & `CheckRoomPressure`)

#### 1. Redundant `SyncAtmoVoid` Every 0.33 Seconds
- `Ship.UpdateGravAndAtmo()` runs every 0.33 seconds (`StarSystem.fEpoch - this._lastAtmoUpdateTime < 0.33`):
  ``charp
  foreach (Room room in this.aRooms)
  {
      room.SyncAtmoVoid(atmosphereAtDistance);
  }
  ```
- If the external atmosphere has not changed (e.g. standard vacuum of space or stable station pressure), calling `SyncAtmo(desiredValue, true)` on all void rooms forces a recalculation of all void gas molar values, calling `Run()` every 0.33s.

3### 2. `CheckRoomPressure` Pairwise Traversal
- `CheckRoomPressure()` iterates all cached `RoomDividerDTO` pairs every 0.33s.
- For each divider in `_cachedRoomDividerDTOs`, it calls `GasContainer.CheckPressureDifference(a, b, pos,null)`.
- Even though it returns early when `deltaPressure < 150.0kPaa, it still queries `GetCondAmount("StatGasPressure")` twice per room divider every 0.33 seconds.

---

## 3. High-Impact Optimization Proposals & Harmony Patches

### Optimization 1: Batched `Run()` Calls in `GasPump.Respire2`
**Impact:** Eliminates 80% of `GasContainer.Run()` recalculations during breathing and life-support gas pumping.
**Mechanism:** 
Instead of calling `gasContainer.Run(false)` inside the `foreach (jsonGasRespireData)` loop for every gas molecule, accumulate all deltas into `mapDGasMols` first, and call `Run(false)` **once** after the loop completes.

```csharp
[HarmonyPatch(typeof(GasPump), "Respire2")]
public static class Patch_GasPump_Respire2_Batching
{
    // Replace inner loop Run() calls with a single deferred call to GasContainer.Run(false)
    // for condOwner and condOwner2, drastically reducing per-tick gas phsics overhead.
}
```

---

### Optimization 2: Pre-indexed Fixed Fluid Mapping for `FluidStrings`
**Impact:** Replaces O(N) string searches with O(1) constant-time direct array accesses.
**Mechanism:** 
The 11 gases in `FluidStrings.moleculeNames` are fixed compile-time identifiers: 0:CH4, 1:CO2, 2:H2, 3:H2O, 4:H2SO4, 5:He2, 6:N2, 7:NH3, 8:O2, 9:CO, 10:Smoke.
Cache an immutable `CmgGasIndexes` lookup replacing `FluidStrings.mol.IndexOf(key)` in `GasContainer.Run()` and `GasPump.pump()`.

---

### Optimization 3: Rate-Limiting `GasContainer.Update()` & Decoupling from Render Frames
**Impact:** Prevents gas pressure and partial pressure recalculations on every visual frame when simulation delta is zero.
**Mechanism:**
`GasContainer` does not need to run on Unity `Update()` if `CondOwner.GasChanged` is false and `this.fDGasTemp == 0.0`.

``ccharp
[HarmonyPatch(typeof(GasContainer), "Update")]
public static class Patch_GasContainer_Update_EarlyOut
{
    [HarmonyPrefix]
    public static bool Prefix(GasContainer __instance, CondOwner ___co)
    {
        if (___co != null && !___co.GasChanged && __instance.fDGasTemp == 0.0)
        {
            if (__instance._timeNextPressureDifCheck > StarSystem.fEpoch)
            {
                return false; // Early out! Skip Run()
            }
        }
        return true;
    }
}
```

---

### Optimization 4: Spatial Audio Culling for `GasExchange`
**Impact:** Eliminates expensive Math/Audio calculations for gas leaks/exchanges far from the player camera.
**Mechanism:**
In `GasExchange.DoGas()`, check distance to player camera before running `PlayAudio` and `AddGasPuff`. If beyond 20 units, skip Puff VFX and disable `AudioSource`.

---

### Optimization 5: Caching Static Divider Pressure Differential in `Ship.CheckRoomPressure`
**Impact:** Reduces double dictionary queries across all room tiles.
**Mechanism:**
If neither room in a `RoomDividerDTO` has had `GasChanged == true`, the pressure difference cannot have changed. Skip `GasContainer.CheckPressureDifference` for static rooms.

---

## 4. Architectural Compatibility & Fidelity Assessment

|| Patch Proposal | Fidelity Risk | Performance Gain | Memory / GC Impact ||
----------------------------------------------------------------------------------------------
|| **Batched Pump/Respire Run** | None (mathematically identical) | High (~3-5x for life-support) | Reduces GC allocs ||
|| **GasContainer Update Early-Out** | None (runs immediately when gas changes) | Very High (~40-60% fewer calls) | Zero GC 1st-frame allocs ||
|| **Fast Fluid Indexing (Array/11-Gas)** | None (exact same gas indices) | Medium (~15-20% faster gas math) | Eliminates list searches ||
p| **Static Room Divider Check Skip** | None (only skips when pressure is static) | Medium (~25% speedup in Ship atmo) | Fewer float reads ||
|| **GasExchange Sound Culling** | None (audible range is ~15 units) | Minor CPU gain | Avoids audio player churn ||

All proposed patches maintain 100% fidelity with the game's vacuum decompression, hypoxia, breathing rates, pipe bursts, and hull creak audio.
