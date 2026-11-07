# Handoff: Audio Changes in Fastranauts Mod

- **Date:** 2026-11-07
- **Topic:** Audio-affecting modifications in Fastranauts mod codebase

## Findings Summary
1. **Direct Patch: `AudioATC.Update`**
   - **File:** `Fastranauts/src/Modules/PauseAndTimeScaleOptimizationModule.cs`
   - **Patch:** Harmony Prefix targeting `AudioATC.Update` with `SkipWhenPaused_Prefix`.
   - **Effect:** Prevents radio/ATC voice line timers (`fTimeLeft`) from ticking down and triggering dialogue while `CrewSim.Paused` is true.

2. **Indirect Audio Effects:**
   - `GasPump.Update` and `GasExchange.Update` tick-rate decoupling (in `UpdateDecouplingAndSimTickModule.cs`). Gas audio emissions (`HandleActivePumpAudio`, `PlayAudio`, `LerpAudioTo`) run on calibrated epoch ticks rather than per-frame.

3. **Non-implemented Proposals:**
   - Notes in `Notes/02_Atmosphere_and_Gas_Analysis.md` proposed spatial audio culling for distant `GasExchange` leaks, but this was omitted from the implementation.

