# Handoff: Fastranauts v1.1.0 — Pause + 16x TimeScale Fix & Update/Sim Tick Decoupling

**Date:** 2026-11-05  
**Plugin:** `Fastranauts Performance Optimizer` (`com.fastranauts.performance` v1.1.0)  
**Compiled Binary:** `c:\Users\Admin\Desktop\Ost Mod\Fastranauts\BepInEx\plugins\Fastranauts\Fastranauts.dll`  
**Analysis Note:** `c:\Users\Admin\Desktop\Ost Mod\Notes\13_Pause_TimeScale_and_Update_System_Analysis.md`  
**Implementation Report:** `c:\Users\Admin\Desktop\Ost Mod\Notes\12_Fastranauts_Implementation_and_Verification.md`

---

## 1. Root Cause Analysis Summary

### Why Setting Speed to `16x` While `PAUSED` Caused `PDAVisualisers::Update` to Jump From `0.9ms` to `30ms`
1. **Pause Only Zeroes `CrewSim.fTimeCoeffPause`, Leaving `Time.timeScale = 16f`**:
   - In `CrewSim.cs:8233-8267` and `Ostranauts/UI/CrewBar/GUITimeBar.cs`, *Ostranauts* stores the selected game speed `[0.25, 0.5, 1, 2, 4, 8, 16]` directly in Unity's global `Time.timeScale` even while `CrewSim.Paused == true`.
   - Consequently, while paused at `16x`, Unity's `Time.deltaTime` (`= Time.unscaledDeltaTime * 16f`) is **16x larger** than real frame time, and Unity's `FixedUpdate` / PhysX loop executes **16x more times per real second (800 Hz)**.
2. **`PDAVisualisers.Update()` Used `Time.deltaTime` Without Checking `CrewSim.Paused`**:
   - `PDAVisualisers.Update()` (`PDAVisualisers.cs:28-43`) incremented `this._pingTime += Time.deltaTime` and called `NotifyShips()` whenever `this._pingTime > 0.5f`.
   - At `16x` speed (even while `PAUSED`), `Time.deltaTime` is `~0.27s–0.53s` per frame, causing `_pingTime > 0.5f` to trigger **32 times per real second (nearly every rendered frame)** instead of 2 times per real second (`0.9ms * 32 ≈ 28.8ms–30ms`), sweeping all 4,000+ ship `CondOwner`s and items while the game simulation was completely frozen.

### Why Other `Update` Methods Degraded During Pause and High Speed (`2x, 4x, 8x, 16x`)
1. **UI & Device Polling Timers Coupled to `Time.deltaTime`**:
   - `GUIBreaker.Update`, `GUIOutput.Update` (which also called `DataHandler.LoadPNG` and `Sprite.Create` on every poll!), `GUIComputerReadout.Update`, `GUICrewNoise.Update`, `GUINoise.Update`, `GUILamp.Update`, `GUILedMeter.Update`, `GUI7Seg.Update`, and `WorkManager.Update` all used `Time.deltaTime`, polling 16x faster at `16x` speed even when paused.
2. **Micro-Stepping & Per-Sub-Step Visual Refresh in `CrewSim.Update` / `CondOwner.UpdateManual`**:
   - `CrewSim.Update()` (`CrewSim.cs:865-880`) clamped its sub-step slice `num3` to `0.004s` (4ms) whenever `CrewSim.aTickers.FirstOrDefault().fNextTickerSecs < num3` on an unsorted list. At `16x` speed (`num ≈ 0.27s–0.53s`), `CrewSim.Update` executed **70–130+ sub-steps per frame**.
   - Every sub-step called `CrewSim.UpdateICOs()`, copying all `aTickers` into `aTickersTemp` and calling `CondOwner.UpdateManual(10)` on **all** `CondOwner`s, even the ~98% whose `fNextTickerSecs > 0.0`.
   - At the end of `CondOwner.UpdateManual` (`CondOwner.cs:401-406`), `RefreshAnim()`, `UpdateStats()`, and `Item.VisualizeOverlays(false)` executed on **every sub-step** (up to 130 times per rendered frame!), and `UpdateStats()` / `GetDamageRate()` performed `FetchDamageConds()` dictionary lookups on undamaged items every time.
3. **Epoch-Parameterized Simulation `MonoBehaviour.Update()` Components Running Every Frame**:
   - `BodyTemp`, `Heater`, `Wound`, `Sensor`, `GasPump`, `GasExchange`, `Powered`, `FusionIC`, `Trader`, `Rotor`, `GasPressureSense`, and `GasContainer` ran Unity `Update()` every frame while paused, and polled at full frame rate during `2x–16x` speed even though their simulation math is already parameterized by elapsed simulation epoch (`StarSystem.fEpoch - fLastEpoch`).
   - `GasContainer.CheckPressureDifference()` on rooms (`IsRoom`) compared each room's pressure against itself (`diff == 0`), wasting spatial room lookups 16 times/sec per room at `16x` speed.

---

## 2. Implemented Solutions

### Subagent 1: Pause Fix & UI/TimeScale Decoupling
- **Updated `c:\Users\Admin\Desktop\Ost Mod\Fastranauts\src\Modules\PdaOverlayOptimizationModule.cs`**:
  - Transpiled `PDAVisualisers.Update` so `_pingTime` uses `GetPdaPingDeltaTime()`: returns `0f` when `CrewSim.Paused` is `true` (eliminating periodic `NotifyShips()` sweeps while paused, while keeping manual `bNotifyShips` preset/slider clicks immediate) and returns `Time.unscaledDeltaTime` when unpaused (keeping PDA overlay sweeps at 2 Hz real-time at `16x` speed instead of 32 Hz).
  - Updated `Ship_VisualizeOverlays_Prefix` to skip periodic pings while paused and throttle Default-mode (`OverlayVariable == "_None"`) periodic damage sweeps to 0.5 Hz real-time (`2.0s` unscaled interval).
- **Created `c:\Users\Admin\Desktop\Ost Mod\Fastranauts\src\Modules\PauseAndTimeScaleOptimizationModule.cs`**:
  - Synchronizes `Time.fixedDeltaTime = 0.02f * Mathf.Clamp(Time.timeScale, 1f, 16f)` on `CrewSim.ResetTimeScale`, `CrewSim.TimeScaleMult`, and `CrewSim.set_Paused` so Unity's `FixedUpdate` / PhysX loop stays at 50 Hz real-world time instead of 800 Hz at `16x`.
  - Pause-gates `TargetLine.FixedUpdate`, `StarSystem.Update(0.0)`, `WorkManager.Update`, `AudioATC.Update`, and idle `Electrical.Update`.
  - Transpiles `GUIBreaker.Update`, `GUICrewNoise.Update`, `GUINoise.Update`, `WorkManager.Update`, `GUIOutput.Update`, `GUIComputerReadout.Update`, `GUILamp.Update`, `GUILedMeter.Update`, and `GUI7Seg.Update` from `Time.deltaTime` to pause-aware `Time.unscaledDeltaTime`.
  - Transpiles `GUIOutput.Update` and `GUIOutput.LoadFromConnection` to cache `Sprite` instances per `Texture2D` (`GetOrCreateCachedSprite`) instead of calling `Sprite.Create` on every poll tick.
  - Transpiles `Ostranauts.COCommands.Rotor.Animate` to remove the duplicate `Time.timeScale` multiplication (`Time.deltaTime * Time.timeScale = 256x` at `16x`).

### Subagent 2: Update Decoupling & Simulation Tick System
- **Created `c:\Users\Admin\Desktop\Ost Mod\Fastranauts\src\Modules\UpdateDecouplingAndSimTickModule.cs`**:
  - Transpiles `CrewSim.Update` to replace the rigid `0.004s` sub-step floor with `GetAdaptiveMinSubstepSeconds()`, scaling the minimum sub-step with `Time.timeScale` and capping `CrewSim.Update` at at most 12 sub-steps per rendered frame.
  - Transpiles `CrewSim.UpdateICOs` to `CrewSim_UpdateICOs_Fast`, eliminating `aTickersTemp.AddRange` allocations and skipping `CondOwner.UpdateManual(10)` on idle `CondOwners` (`fNextTickerSecs > 0.0` and not due for real-time-throttled `Cleanup()`).
  - Transpiles `CondOwner.UpdateManual` so `RefreshAnim()`, `UpdateStats()`, and `Item.VisualizeOverlays(false)` execute at most once per rendered frame (`Time.frameCount`) per `CondOwner`.
  - Fast-paths `CondOwner.UpdateStats` and `CondOwner.GetDamageRate` for undamaged items (`!mapConds.ContainsKey("StatDamage")`).
  - Pause-guards and real-time-throttles (`Time.unscaledTime` with 8-bucket instance staggering at `>1x` speed) epoch-driven simulation `MonoBehaviour.Update()` loops (`BodyTemp`, `Heater`, `Wound`, `Sensor`, `GasPump`, `GasExchange`, `Powered`, `FusionIC`, `Trader`, `Rotor`, `GasPressureSense`, `GasContainer`), and skips self-comparing room checks in `GasContainer.CheckPressureDifference()`.

---

## 3. Verification & Workspace Status
- Built `c:\Users\Admin\Desktop\Ost Mod\Fastranauts\src\Fastranauts.csproj` in `Release` mode (`0 Warning(s), 0 Error(s)`).
- Deployed `Fastranauts.dll` (`v1.1.0.0`) to `c:\Users\Admin\Desktop\Ost Mod\Fastranauts\BepInEx\plugins\Fastranauts\Fastranauts.dll`.
- Verified all 48 target methods and fields in `Assembly-CSharp.dll` via reflection (`Missing: 0`).
- Cleaned up all lock files in `c:\Users\Admin\Desktop\Ost Mod\Progress\`.
