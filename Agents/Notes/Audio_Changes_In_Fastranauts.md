# Audio-Affecting Changes in Fastranauts Mod

## Summary
The Fastranauts mod touches audio in two distinct ways:

1. **Direct Audio Hook: `AudioATC.Update` Pause-Gating**
   - **Location:** `Fastranauts/src/Modules/PauseAndTimeScaleOptimizationModule.cs` (lines 146-157, 286-294)
   - **Patch Type:** Harmony Prefix (`SkipWhenPaused_Prefix`)
   - **Vanilla Behavior:**
     In the base game (`AudioATC.cs` line 14):
     ```csharp
     private void Update()
     {
         if (this.srcVoiceNow == null) return;
         this.fTimeLeft -= Time.deltaTime;
         if (this.fTimeLeft <= 0f)
         {
             this.GetNextLine(0.0);
         }
     }
     ```
     Because `AudioATC.Update()` directly decremented `fTimeLeft -= Time.deltaTime`, when the game was paused (`CrewSim.Paused == true`), `fTimeLeft` continued to tick down, firing the next ATC voice line dialogue queue while time was frozen.
   - **Mod Change:**
     When `CrewSim.Paused` is true, `AudioATC.Update` is skipped entirely. This freezes the dialogue timer `fTimeLeft` while paused so radio/ATC dialogue does not progress during game pause.

2. **Indirect / Audio Producer Rate Effects**
   - **GasPump & GasExchange Sim Rate Throttling / Decoupling:**
     - In `UpdateDecouplingAndSimTickModule.cs`, `GasPump.Update` and `GasExchange.Update` run on staggered epochs (every 0.12s / 0.10s) rather than unconstrained every render frame, and are skipped when paused.
     - While audio calls (`HandleActivePumpAudio`, `PlayAudio`, `LerpAudioTo`) are maintained within these updates, their polling cadence is stabilized at high simulation speeds.
   - **Unimplemented Audio Proposal (Found in Notes only):**
     - `Notes/02_Atmosphere_and_Gas_Analysis.md` (lines 73-76, 184-187) contains a design proposal for "Spatial Audio Culling for GasExchange" (culling `PlayAudio` and leak sound calculations for rooms > 20 units away from the player camera). This optimization was analyzed in research notes but **not implemented** in the Fastranauts codebase.

