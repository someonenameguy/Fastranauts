# 13. Pause Issue, TimeScale (0.25x–16x), and `Update` System Deep-Dive Analysis

**Date:** 2026-11-05  
**Scope:** Root-cause analysis and architectural design for fixing:
1. **The Pause + High-Speed (`16x`) Performance Bug** (where setting game speed to `16x` while `PAUSED` causes `PDAVisualisers::Update` to jump from `0.9ms` to `30ms` and spikes other `Update` methods).
2. **Decoupling / Replacing Functions in `Update` & the Time Speed System** so functions that do not need to run faster during speed-up (`[0.25, 0.5, 1, 2, 4, 8, 16]`) are moved out of per-substep/per-frame `Update` or decoupled from `Time.timeScale`.

---

## 1. Root Cause Analysis: Why Setting `16x` While `PAUSED` Spikes `PDAVisualisers::Update` (0.9ms $\rightarrow$ 30ms) & Other `Update`s

### A. How Ostranauts Implements Pause vs. Speed Control (`CrewSim.cs`)
In `CrewSim.cs` (`lines 5065–5105` and `8233–8267`), the game controls simulation speed `[0.25, 0.5, 1, 2, 4, 8, 16]` and pause state as follows:
```csharp
public static void TimeScaleMult(float fMultiplier)
{
    Time.timeScale = MathUtils.Clamp(Time.timeScale * fMultiplier, 0.25f, 16f);
    CrewSim.OnTimeScaleUpdated.Invoke();
}

public static float TimeElapsedScaled()
{
    return Time.deltaTime * CrewSim.fTimeCoeffPause;
}

public static bool Paused
{
    get { return CrewSim.fTimeCoeffPause == 0f; }
    set
    {
        ...
        CrewSim.fTimeCoeffPause = (value ? 0f : 1f);
        Time.timeScale = MathUtils.Clamp(Time.timeScale, 0.25f, 16f);
        CrewSim.OnTimeScaleUpdated.Invoke();
        ...
    }
}
```

### B. The 3 Fatal Flaws in This Design
1. **`Time.timeScale` Remains at `16f` While `CrewSim.Paused == true`**:
   - Because `GUITimeBar` and `CrewSim.TimeScaleMult` store the user's chosen speed multiplier directly in `UnityEngine.Time.timeScale`, pausing the game (`CrewSim.Paused = true`) only sets `CrewSim.fTimeCoeffPause = 0f` and **leaves `Time.timeScale` at `16f`**!
   - In Unity, `Time.deltaTime = Time.unscaledDeltaTime * Time.timeScale`.
   - Therefore, when the player sets speed to `16x` **even while `PAUSED`**, `Time.deltaTime` becomes **16x larger** than real frame time (`~0.27s–0.53s` per frame)!
2. **Why `PDAVisualisers::Update` Jumps From `0.9ms` to `30ms` at `16x` While `PAUSED` (`PDAVisualisers.cs:28-43`)**:
   ```csharp
   private void Update()
   {
       ...
       this._pingTime += Time.deltaTime;
       if (this.bNotifyShips || (this._togglePingUpdates && this._pingTime > 0.5f))
       {
           this._pingTime = 0f;
           this.NotifyShips();
       }
   }
   ```
   - **Flaw 1 (No Pause Check)**: `PDAVisualisers.Update()` never checks `CrewSim.Paused`. While paused, ship conditions (`_Power`, `_Heat`, `_Pressure`, `_Damage`, `O2`) are completely frozen unless the user explicitly clicks a preset/slider (`bNotifyShips == true`). Yet `_pingTime` keeps triggering `NotifyShips()`, iterating all 4,000+ `CondOwner` items on the ship and running `Item.VisualizeOverlays(false)` (`GetRoomAtWorldCoords1`, `GetCondAmount`, `GetDamageRate`, `InverseLerp`) on every item!
   - **Flaw 2 (`Time.deltaTime` Scales 16x With `Time.timeScale`)**: At `1x` speed, `_pingTime > 0.5f` triggers **2 times per real second** (`~0.9ms` per sweep). When speed is set to `16x` (paused or unpaused), `Time.deltaTime` is multiplied by `16`, so `_pingTime > 0.5f` triggers **32 times per real second (almost every frame)**! `0.9ms × 32 ≈ 28.8ms–30ms`!
3. **Why Many Other `Update`s & `FixedUpdate` Spike at `16x` (Paused & Unpaused)**:
   - **Unity's Internal `FixedUpdate` / Physics Loop**: Because `Time.fixedDeltaTime` stays at `0.02f` while `Time.timeScale` is set to `16f`, Unity executes `FixedUpdate` (`TargetLine.FixedUpdate`) and internal PhysX simulation steps **16x more times per rendered frame (800 Hz)** — even while `CrewSim.Paused == true`!
   - **`StarSystem.Update(0.0)` Runs Every Frame While Paused (`CrewSim.cs:884`, `StarSystem.cs:802`)**: Even when `CrewSim.Paused == true` (`num == 0.0`), `CrewSim.Update()` calls `CrewSim.system.Update(0.0)`, which updates all `BodyOrbit`s, allocates `dictShips.Values.ToList<Ship>()`, runs `UpdateShip(0.0, ship)` on every ship, and runs `UpdateDockingGroups(0.0)` every frame for zero simulation progress.
   - **UI & Device `Update()` Timers Using `Time.deltaTime`**:
     - `GUIBreaker.Update()` (`checkTime += Time.deltaTime`)
     - `GUIOutput.Update()` (`checkTime += Time.deltaTime` — worse, every tick calls `DataHandler.LoadPNG` and `Sprite.Create`, allocating a new `Sprite` 16x faster at `16x` even while paused!)
     - `GUIComputerReadout.Update()` (`intervalTiming -= Time.deltaTime`)
     - `GUICrewNoise.Update()` & `GUINoise.Update()` (`durationsincelastupdate += Time.deltaTime`)
     - `WorkManager.Update()` (`Time.deltaTime * num2` — iterates all `Task2` items and checks `InterruptForWork` even while paused!)
     - `AudioATC.Update()` (`fTimeLeft -= Time.deltaTime` — ticks down while paused!)
   - **Ship Simulation `MonoBehaviour.Update()` Methods Running Every Frame While Paused**:
     - `GasContainer.Update()`, `GasPressureSense.Update()`, `GasExchange.Update()`, `GasPump.Update()`, `BodyTemp.Update()`, `Sensor.Update()`, `Heater.Update()`, `Wound.Update()`, `FusionIC.Update()`, `Powered.Update()`, `Electrical.Update()`, `Trader.Update()`, `Rotor.Update()`, `Pathfinder.Update()` all execute Unity `Update()` every frame even when `CrewSim.Paused == true`.

---

## 2. Root Cause Analysis: Why Functions Update Unnecessarily Fast During Speed-Up (`2x, 4x, 8x, 16x`) & How to Replace/Decouple Them

When unpaused at high speed (`2x, 4x, 8x, 16x`), three systemic issues cause non-simulation and rate-independent functions to execute 16x–200x more often per real second:

### A. Visual, Stat & Cleanup Functions Inside `CondOwner.UpdateManual` Running on Every Sub-Step (`CrewSim.cs:865-880`, `CondOwner.cs:279-407`)
In `CrewSim.Update()`:
```csharp
double fEpoch = StarSystem.fEpoch;
double num = (double)(Time.deltaTime * CrewSim.fTimeCoeffPause);
double num3;
for (double num2 = num; num2 > 0.0; num2 -= num3)
{
    num3 = num2;
    if (CrewSim.aTickers.Count > 0 && CrewSim.aTickers.FirstOrDefault().fNextTickerSecs < num3)
    {
        num3 = CrewSim.aTickers.FirstOrDefault().fNextTickerSecs;
    }
    if (num3 < 0.004)
    {
        num3 = 0.004;
    }
    StarSystem.fEpoch += num3;
    this.AdvanceSim((float)num3);
}
```
1. **Unsorted `aTickers.FirstOrDefault()` Micro-Stepping**:
   - `CrewSim.aTickers` is an unsorted `UniqueList<CondOwner>`. If `aTickers[0]` has `fNextTickerSecs <= 0.004`, `num3` is forced to `0.004s` (4ms).
   - At `16x` speed (`num ≈ 0.27s–0.53s`), this loop runs **70 to 130+ sub-steps in a single rendered frame**!
2. **Per-Sub-Step Full-List Copy & Unfiltered `UpdateManual(10)`**:
   - In every sub-step, `UpdateICOs()` copies `CrewSim.aTickers` into `CrewSim.aTickersTemp` and calls `condOwner.UpdateManual(10)` on **every** `CondOwner` in `CrewSim.aTickers` — even the 98% whose `fNextTickerSecs > 0.0` (no expired ticker in that sub-step)!
3. **Visual/Presentation Functions Called at the End of Every `CondOwner.UpdateManual` (`CondOwner.cs:401-406`)**:
   ```csharp
   this.RefreshAnim();
   this.UpdateStats();
   if (this.Item != null)
   {
       this.Item.VisualizeOverlays(false);
   }
   ```
   - Even when **no ticker expired** on a `CondOwner` during a sub-step, `CondOwner.UpdateManual` still calls `RefreshAnim()`, `UpdateStats()`, and `Item.VisualizeOverlays(false)`!
   - Worse, in `CondOwner.UpdateStats()` and `CondOwner.GetDamageRate()` (`CondOwner.cs:9986-10029`), for any undamaged item (`_statDamage == null`, which is 99% of items), `if (_statDamageMax == null || _statDamage == null)` is **always true**, causing `FetchDamageConds()` (2 dictionary lookups) and `this.mapInfo["Condition"] = "100%"` to run on **every single call**!
   - **Architectural Replacement**:
     - Move `RefreshAnim()`, `UpdateStats()`, and `Item.VisualizeOverlays(false)` **out of the sub-step loop**! Only run them **at most once per rendered frame**, and only on `CondOwner`s where a ticker actually fired during that frame!
     - Replace `CrewSim.Update()`'s naive 4ms micro-stepping with a smart, bounded sub-stepper (`MaxSubstepsPerFrame` with adaptive minimum step scaled by `Time.timeScale`) and zero-allocation `UpdateICOs` that only ticks `CondOwner`s whose earliest ticker has expired (`fNextTickerSecs <= 0.0`).
     - Throttle `CondOwner.Cleanup()` by real unscaled time (`>= 2.0s` real time) in addition to `StarSystem.fEpoch` so `16x` speed does not run dictionary/ticker cleanup 8x per real second on every `CondOwner`.

### B. Redundant Dual-Update (`MonoBehaviour.Update()` + `IManUpdater.UpdateManual()`) & Delta-Parameterized Epoch Components Scaling 16x Faster
1. **Delta-Parameterized Simulation Components Running 16x–60x Per Real Second at `16x` Speed**:
   - Look at `BodyTemp.Update()`, `Heater.Update()`, `Wound.Update()`, `GasPump.Update()`, `Powered.Update()`, `FusionIC.Update()`, and `GasExchange.Update()`:
   - Every one of these components **already scales its simulation math by actual elapsed simulation time** (`double elapsed = StarSystem.fEpoch - lastEpoch`)!
     - `BodyTemp`: `this.Exposure(num / 3600.0)`
     - `Heater`: `this.Heat(num)`
     - `Wound`: `this.Run(num)`
     - `GasPump`: `float num2 = Convert.ToSingle(num / (double)this.gr.fSignalCheckRate); this.Respire2(num2); this.Pump(num2);`
     - `Powered`: `num *= StarSystem.fEpoch - this.fUpdateLast; this.UsePower(condOwner, num);`
     - `FusionIC`: `this.Run(StarSystem.fEpoch - this.fTimeLastRun);`
     - `GasExchange`: `this.DoGas(num);`
   - Because they gate execution using only simulation seconds (`if (StarSystem.fEpoch - last >= 1.0)` or `0.27s`), at `1x` speed they run at `1 Hz` (`3.7 Hz` for `FusionIC`), which is lightweight.
   - **However, at `16x` speed**, `1.0` simulation second passes in `0.0625` real seconds, forcing all of them to execute **16 times per real second (or 60 times per real second for `FusionIC` and `Powered.fMinorUpdate`)**, PLUS getting called redundantly from both Unity's `MonoBehaviour.Update()` and `CondOwner.UpdateManual()`!
   - **Architectural Replacement**:
     - Decouple Unity `MonoBehaviour.Update()` from `IManUpdater.UpdateManual()` and enforce a minimum **real-world unscaled time interval** (e.g., capping high-speed execution frequency to a smooth real-time rate like 4–10 Hz at `4x–16x` speed while passing the exact accumulated `StarSystem.fEpoch - lastEpoch` delta into `Exposure`/`Heat`/`Run`/`Respire2`/`Pump` so simulation accuracy is 100% preserved!).
     - For `GasContainer.Update()`:
       - Skip completely when `CrewSim.Paused` is true.
       - Skip `CheckPressureDifference()` on room `CondOwner`s (`co.HasCond("IsRoom")`) where `this == room.CO.GasContainer` (`a == b` always has `0` pressure difference!), and rate-limit `CheckPressureDifference()` by real unscaled time (`>= 0.5s` real time) so it doesn't run 16x per real second at `16x` speed!
       - Eliminate the useless `new List<CondOwner>()` allocation in `GasContainer.CheckPressureDifference(a, b, coPos, coContainer)`.

### C. Per-Frame Unchanged Redraws in `CrewSim.Update()` (`DrawPower`, `BuildButtonList`)
- `CrewSim.DrawPower(Ship)` transforms up to 7,600 tile positions via `camMain.WorldToScreenPoint` and rebuilds Vectrosity meshes (`linePower.Draw()`, `linePowerOff.Draw()`) every frame when power lines are toggled on.
- `Rotor.Animate()` multiplies rotation by `Time.deltaTime * Time.timeScale` (double-scaling by `Time.timeScale^2` = 256x at 16x speed!).

---

## 3. Subagent Task Partitioning (Collision-Free Execution Plan)

To implement and verify these improvements cleanly without file collisions:

1. **Subagent 1 (`PauseAndTimeScaleModule` Specialist)**:
   - **Target Files**:
     - Create `c:\Users\Admin\Desktop\Ost Mod\Fastranauts\src\Modules\PauseAndTimeScaleOptimizationModule.cs`
     - Update `c:\Users\Admin\Desktop\Ost Mod\Fastranauts\src\Modules\PdaOverlayOptimizationModule.cs`
   - **Responsibilities**:
     - Fix `PDAVisualisers.Update()` pause bug & `16x` speed bug: stop `_pingTime` accumulation when `CrewSim.Paused` is true (still allowing immediate `bNotifyShips` user preset clicks while paused), and use `Time.unscaledDeltaTime` for `_pingTime` so `16x` speed never accelerates PDA overlay pings beyond 2 Hz real time.
     - In `PdaOverlayOptimizationModule`, skip periodic `_pingTime` ship scans when `OverlayVariable == "_None"` (Default preset) and optimize room lookup caching during `Ship.VisualizeOverlays`.
     - Synchronize `Time.fixedDeltaTime` with `Time.timeScale` (`0.02f * Mathf.Clamp(Time.timeScale, 1f, 16f)`) in `CrewSim.ResetTimeScale`, `CrewSim.TimeScaleMult`, and `CrewSim.set_Paused` so Unity's `FixedUpdate` / PhysX loop does not run 16x more often at `16x` speed.
     - Guard `TargetLine.FixedUpdate`, `StarSystem.Update(0.0)`, `WorkManager.Update`, `AudioATC.Update`, and `Electrical.Update` while `CrewSim.Paused == true`.
     - Decouple UI/device `Update()` timers (`GUIBreaker.Update`, `GUIOutput.Update`, `GUIComputerReadout.Update`, `GUICrewNoise.Update`, `GUINoise.Update`, `WorkManager.Update`) from `Time.deltaTime` to `Time.unscaledDeltaTime` (and pause-gate where appropriate), plus fix `GUIOutput.Update` repeated `Sprite.Create` allocations by caching sprites per portrait name.

2. **Subagent 2 (`UpdateDecouplingAndSimTickModule` Specialist)**:
   - **Target Files**:
     - Create `c:\Users\Admin\Desktop\Ost Mod\Fastranauts\src\Modules\UpdateDecouplingAndSimTickModule.cs`
   - **Responsibilities**:
     - Replace `CrewSim.Update`'s naive 4ms sub-stepping loop and `CrewSim.UpdateICOs` with bounded smart sub-stepping (`AdvanceSimSubstepsFast`) and zero-allocation `UpdateICOs` that only calls `UpdateManual` on `CondOwner`s whose earliest ticker is due (`fNextTickerSecs <= 0.0`) or due for cleanup.
     - Move `CondOwner.RefreshAnim()`, `CondOwner.UpdateStats()`, and `Item.VisualizeOverlays(false)` **out of the sub-step loop** in `CondOwner.UpdateManual` so they execute at most **once per rendered frame** per ticked `CondOwner`, and eliminate per-sub-step `aMUs`/`aMUDels` list copying.
     - Fix `CondOwner.UpdateStats()` and `CondOwner.GetDamageRate()` dictionary lookup thrashing on undamaged items (`_statDamage == null`).
     - Pause-guard and real-time-throttle (`Time.unscaledTime`) the epoch-driven `MonoBehaviour.Update()` / `IManUpdater` components (`GasContainer`, `GasPressureSense`, `GasExchange`, `GasPump`, `BodyTemp`, `Heater`, `Wound`, `Sensor`, `Powered`, `FusionIC`, `Rotor`, `Trader`) so they do not run while `CrewSim.Paused == true` and do not multiply CPU usage by 16x–60x during `4x–16x` time acceleration (while preserving exact elapsed `StarSystem.fEpoch` simulation delta math!).
     - Skip useless self-comparison `GasContainer.CheckPressureDifference()` on rooms and eliminate its `new List<CondOwner>()` allocation.

3. **Coordinator Integration & Verification**:
   - Register the new modules in `FastranautsPlugin.cs` and `FastStats.cs`.
   - Build `Fastranauts.csproj` in `Release` mode, run reflection verification against `Assembly-CSharp.dll`, update `Notes/` and `Handoffs/`, and clean up `Progress/`.
