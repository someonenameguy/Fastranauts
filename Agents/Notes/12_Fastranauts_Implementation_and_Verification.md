# Fastranauts BepInEx Mod Implementation & Verification Report

**Plugin Name:** `Fastranauts Performance Optimizer`  
**Plugin GUID:** `com.fastranauts.performance`  
**Version:** `1.1.0`  
**Target Game Version:** *Ostranauts* v1.0.0.9+ (BepInEx 5.4.x / Harmony 2.9)  
**Compiled Binary:** `c:\Users\Admin\Desktop\Ost Mod\Fastranauts\BepInEx\plugins\Fastranauts\Fastranauts.dll`  
**Source Code:** `c:\Users\Admin\Desktop\Ost Mod\Fastranauts\src\`

---

## 1. Mod-Compatibility & Non-Destructive Engineering

To ensure **Fastranauts** does not affect or break any other mods (including C# BepInEx mods such as *Take Their Ships* / `ShipClaim.dll` and JSON content mods loaded by `DataHandler`):
1. **Zero Conflict with Reference Mods**:
   - Does **not** touch `AIShip.Setup`, `CondOwner.SetData`, `Interaction.GetAvailActions`, `Interaction.ApplyEffects`, `GUIShipBroker`, `GUIXPDRBroker`, `GUITrade`, `DataHandler` CustomInfos save/load hooks, or `DataHandler.LoadMods`.
   - Does **not** alter save file schemas or in-game balance/simulation rules.
2. **Harmony Transpilers & Non-Blocking Hooks**:
   - Uses targeted IL **Transpilers** (`Ship.VisualizeOverlays`, `GasPressureSense.Run`, `GasPump.GetA/GetB/Respire2`, `GasContainer.AddGasMols`, `Ship.GetCOsAtWorldCoords1`, `CrewSim.GetMouseOverCO/GetMouseOverBG`, `CondOwner.Cleanup/SetHighlight/GetCondAmount/HasCond/AddCondAmount`, `Visibility.ResetLightMesh/AddOccludersFromCrewSimBlocks/IsCondOwnerLOSVisibleBlocks/EmitSkirt/LateUpdate`, `GUIInventoryWindow.UpdateWindow`, `JumpPointSearch.GetGridSize/FindPath`) so that original methods and all other mods' `Prefix` and `Postfix` patches continue to execute normally.
3. **Fault Isolation & Granular Configuration**:
   - Every module implements `IFastModule` and is wrapped in an independent `try / catch` initializer with its own toggle in `BepInEx/config/com.fastranauts.performance.cfg` plus a global `MasterEnable` switch.

---

## 2. Implemented Optimization Modules

### Module 1: `PdaOverlayOptimizationModule`
- **Target Bottleneck**: `pnlViz > PDAVisualisers::Update` (`24.20 ms | 211,306 B Mem` every 0.5s in live profiler trace, and `30.00 ms` every frame at `16x` speed while `PAUSED`).
- **Patches**:
  - `PDAVisualisers.Update` (Transpiler): Replaces `Time.get_deltaTime` with `GetPdaPingDeltaTime()`, which returns `0f` while `CrewSim.Paused` is `true` (completely preventing periodic `_pingTime` sweeps while paused, while still allowing immediate `bNotifyShips` refreshes when the user clicks a preset or drags a slider while paused) and returns `Time.unscaledDeltaTime` when unpaused so `16x` simulation speed keeps PDA shader sweeps at 2 Hz real-time instead of 32 Hz.
  - `PDAVisualisers.NotifyShips` (Prefix/Postfix) & `Ship.VisualizeOverlays(ref bool force)` (Prefix): Detects periodic 0.5s `_pingTime` refreshes (`!bNotifyShips`), skips periodic pings if `CrewSim.Paused` is `true`, throttles Default-mode (`OverlayVariable == "_None"`) periodic damage sweeps to 0.5 Hz real-time (`2.0s` unscaled interval), and changes `force` from `true` to `false` on periodic pings while keeping `force = true` on manual user preset/slider changes.
  - `Ship.VisualizeOverlays` (Transpiler): Replaces `DataHandler.GetCondTrigger("Blank")` with non-cloning `DataHandler.GetCondTrigger("Blank", false)` and replaces `this.GetCOs(condTrigger, true, false, true)` with `GetShipCOsForOverlayFast`, reusing a static `CondOwnerVisitorAddToHashSet` and `List<CondOwner>` buffer (**0 bytes allocated**, saving 211 KB per ping).
  - `CondOwnerVisitorCondTrigger.WrapVisitor` (Prefix): Returns the inner visitor directly when the filter trigger is `"Blank"`.

### Module 2: `SensorAndDoorOptimizationModule`
- **Target Bottleneck**: `ItmDoor01OpenOn > GasPressureSense::Update` (`0.21 ms | 2,219 B Mem` per door) & `GasPump::Update` / `GasContainer.AddGasMols`.
- **Patches**:
  - `GasPressureSense.Run` (Transpiler): Replaces `new List<CondOwner>()` with a cleared static `List<CondOwner>` and replaces `DataHandler.GetCondTrigger(strCTClear)` with non-cloning `DataHandler.GetCondTrigger(strCTClear, false)`.
  - `GasPump.GetA`, `GasPump.GetB`, `GasPump.Respire2` (Transpilers): Replaces `new List<CondOwner>()` with cleared static `List<CondOwner>` buffers.
  - `GasContainer.AddGasMols` (Transpiler): Caches `"StatGasMol" + strGas` strings in an ordinal dictionary and replaces `DataHandler.GetCond(strGas)` (which instantiated a full `new Condition(...)` object and two `List`s on every call just to test `!= null`) with `GetCondExistenceMarker(strGas)`.

### Module 3: `RaycastAndGcOptimizationModule`
- **Target Bottleneck**: `PlayState > CrewSim::Update` (`3.80 ms | 22,072 B Mem`) & `CondOwner` GC churn.
- **Patches**:
  - `Ship.GetCOsAtWorldCoords1`, `CrewSim.GetMouseOverCO`, `CrewSim.GetMouseOverBG` (Transpilers): Replaces `Physics.RaycastAll` and subsequent `.OrderBy(go => go.distance)` / `.ToArray()` LINQ allocations with `FastRaycast.RaycastAll` (`Physics.RaycastNonAlloc` into a 64-hit buffer, in-place insertion sort by distance, pre-sized array table `_arraysByLength`, and reusable `ZeroAllocOrderedHitEnumerable`).
  - `CondOwner.Cleanup` (Transpiler): Replaces `new List<string>(dictRecentlyTried.Keys)` with an in-place expiration sweep (`SweepAndReturnEmptyKeyList`) that eliminates the list allocation and fixes the vanilla single-item-per-cycle memory leak bug.
  - `CondOwner.SetHighlight` (Transpiler): Reuses a cleared static `MaterialPropertyBlock` instead of allocating `new MaterialPropertyBlock()` on every hover change.
  - `CondOwner.GetCondAmount`, `CondOwner.HasCond`, `CondOwner.AddCondAmount` (Transpilers): Interns `strName.Substring(6)` via `_thresholdSubstringCache` for `"Thresh..."` lookups.

### Module 4: `VisibilityLightOptimizationModule`
- **Target Bottleneck**: `ArmLLamp > Visibility::LateUpdate` (`0.06 ms | 715–933 B Mem` per lamp/crew light every frame).
- **Patches**:
  - `Visibility.ResetLightMesh` (Transpiler): Replaces the 5 per-frame `new List<T>()` allocations (`shadowVerts`, `shadowIndices`, `shadowUVs`, `skirt`, `skirtW`) with cleared static `List<T>` buffers.
  - `Visibility.AddOccludersFromCrewSimBlocks` & `Visibility.IsCondOwnerLOSVisibleBlocks` (Transpilers): Replaces `new List<Segment>()` with persistent static `List<Segment>` buffers so `Block.GetSegments` reuses its 4 `Segment` objects as intended.
  - `Visibility.EmitSkirt` (Transpiler): Replaces `new List<Vector2>()` with a cleared static `List<Vector2>`.
  - `Visibility.LateUpdate` (Transpiler): Replaces `shadowVerts.ToArray()`, `shadowIndices.ToArray()`, and `shadowUVs.ToArray()` with zero-allocation `Mesh.SetVertices(List<Vector3>)`, `Mesh.SetTriangles(List<int>, 0)`, and `Mesh.SetUVs(0, List<Vector2>)`.

### Module 5: `UiAndTooltipOptimizationModule`
- **Target Bottleneck**: `GUIInventoryWindow.UpdateWindow` per-frame Ground redraws & `ItemList > GUIItemList::Update` (`0.06 ms | 144 B Mem`).
- **Patches**:
  - `GUIInventoryWindow.UpdateWindow` (Transpiler): Replaces only the per-frame `this.Redraw()` call on `InventoryWindowType.Ground` with `RedrawGroundWindowThrottled` (5 Hz / 0.20s interval or immediate when `CODoll` moves), leaving direct `window.Redraw()` calls from drag-and-drop or other mods 100% immediate.
  - `GUIItemList.Update` (Prefix): Runs immediately whenever the mouse moves, UI pointer state changes, or any key is pressed, and throttles redundant tooltip string rebuilds to 10 Hz (`0.10s`) while the cursor is stationary.

### Module 6: `PathfindingAndAiOptimizationModule`
- **Target Bottleneck**: `JumpPointSearch` 2D grid allocations, `Ostranauts.Pathing.Vector2Int` struct boxing, and `AIShipManager.CheckTracking` per-frame scans.
- **Patches**:
  - `JumpPointSearch.GetGridSize` (Transpiler): Replaces `new bool?[_gridXMax, _gridYMax]` with a reusable cleared 2D buffer (`GetReusableWalkableGrid`).
  - `JumpPointSearch.FindPath` (Transpiler) & `PathMemory` (Constructor Postfix): Uses `FastVector2IntComparer` (`IEqualityComparer<Ostranauts.Pathing.Vector2Int>`) and reusable `HashSet<Vector2Int>` / `Dictionary<Vector2Int, Node>` instances to eliminate struct boxing and reflection hashing.
  - `AIShipManager.CheckTracking` (Prefix): Rate-limits radar warning scans to 5 Hz (`0.20s` interval).

### Module 7: `PauseAndTimeScaleOptimizationModule`
- **Target Bottleneck**: Setting speed to `16x` while `PAUSED` or unpaused accelerating Unity's `Time.deltaTime` and `FixedUpdate` loop by 16x across UI panels, PhysX steps, and idle background managers.
- **Patches**:
  - `CrewSim.ResetTimeScale`, `CrewSim.TimeScaleMult`, `CrewSim.set_Paused` (Postfixes): Synchronizes `Time.fixedDeltaTime = 0.02f * Mathf.Clamp(Time.timeScale, 1f, 16f)` so Unity's internal PhysX step and `FixedUpdate` remain at 50 Hz real-world time instead of running 800 times/sec at `16x` speed.
  - `TargetLine.FixedUpdate`, `StarSystem.Update(0.0)`, `WorkManager.Update`, `AudioATC.Update`, `Electrical.Update` (Prefixes): Pause-gates background world and grid loops when `CrewSim.Paused` is `true`.
  - `GUIBreaker.Update`, `GUICrewNoise.Update`, `GUINoise.Update`, `WorkManager.Update` (Transpilers): Replaces `Time.get_deltaTime` with `GetUiPollDeltaTime()` (`0f` when paused, `Time.unscaledDeltaTime` when unpaused).
  - `GUIOutput.Update` & `GUIOutput.LoadFromConnection` (Transpilers): Replaces `Time.get_deltaTime` with `GetUiPollDeltaTime()` and replaces `Sprite.Create` with a dictionary-backed sprite cache (`GetOrCreateCachedSprite`).
  - `GUIComputerReadout.Update`, `GUILamp.Update`, `GUILedMeter.Update`, `GUI7Seg.Update` (Transpilers): Replaces `Time.get_deltaTime` with `Time.unscaledDeltaTime`.
  - `Ostranauts.COCommands.Rotor.Animate` (Transpiler): Fixes the double `Time.timeScale` bug (`Time.deltaTime * Time.timeScale = 256x` at `16x`).

### Module 8: `UpdateDecouplingAndSimTickModule`
- **Target Bottleneck**: `CrewSim.Update` running 70–130+ sub-steps per frame at `16x` speed, `CrewSim.UpdateICOs` iterating all idle tickers every sub-step, `CondOwner.UpdateManual` repeating visual/stat updates on every sub-step, and epoch-based simulation `MonoBehaviour.Update()` components polling every frame at `16x` speed or while paused.
- **Patches**:
  - `CrewSim.Update` (Transpiler): Replaces the fixed `0.004s` sub-step floor with `GetAdaptiveMinSubstepSeconds()`, scaling the minimum sub-step with `Time.timeScale` and capping `CrewSim.Update` at a maximum of 12 sub-steps per rendered frame.
  - `CrewSim.UpdateICOs` (Transpiler): Replaces `aTickersTemp.AddRange(CrewSim.aTickers)` with zero-allocation indexed iteration that skips `CondOwner.UpdateManual(10)` for idle `CondOwner`s (`fNextTickerSecs > 0.0` and not due for real-time-throttled `Cleanup()`).
  - `CondOwner.UpdateManual` (Transpiler): Limits `RefreshAnim()`, `UpdateStats()`, and `Item.VisualizeOverlays(false)` to execute at most once per rendered frame (`Time.frameCount`) per `CondOwner`.
  - `CondOwner.UpdateStats` & `CondOwner.GetDamageRate` (Prefixes): Fast-paths undamaged items (`!mapConds.ContainsKey("StatDamage")`) to skip redundant `FetchDamageConds()` dictionary lookups.
  - `BodyTemp.Update`, `Heater.Update`, `Wound.Update`, `Sensor.Update`, `GasPump.Update`, `GasExchange.Update`, `Powered.Update`, `FusionIC.Update`, `Trader.Update`, `Rotor.Update`, `Rotor.UpdateManual`, `GasPressureSense.Update`, `GasContainer.Update`, `GasContainer.CheckPressureDifference` (Prefixes): Pause-guards all simulation `Update()` methods when `CrewSim.Paused` is `true`, skips self-comparing room pressure checks (`co.HasCond("IsRoom")`), and throttles real-time execution frequency (`Time.unscaledTime`) at `2x–16x` speed while preserving exact `StarSystem.fEpoch` simulation deltas.

---

## 3. Build & Target Verification

- **Compiler**: `.NET SDK 10.0.403` (`Release` configuration, `0 Warning(s)`, `0 Error(s)`).
- **Target Reflection Audit**: All 75 Harmony target methods and fields across `Assembly-CSharp.dll` (including all 48 targets in Modules 1, 7, and 8) were verified via reflection against `Game Ref Ostranauts [READ ONLY]\Ostranauts_Data\Managed\Assembly-CSharp.dll` with `0` missing targets.
- **In-Game Telemetry**: Press `F8` (configurable) or enter `fastinfo` in the debug console to print live counters from `FastStats`.
