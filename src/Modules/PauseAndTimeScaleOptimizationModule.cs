using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx.Configuration;
using BepInEx.Logging;
using Fastranauts.Common;
using HarmonyLib;
using Ostranauts.COCommands;
using Ostranauts.TargetVisualization;
using UnityEngine;

namespace Fastranauts.Modules
{
    /// <summary>
    /// Fixes the Pause + High-Speed (16x) performance spikes and decouples UI/device Update()
    /// timers from Time.timeScale:
    ///
    /// 1. Synchronizes Time.fixedDeltaTime with Time.timeScale (0.02f * Clamp(timeScale, 1f, 16f))
    ///    when speed or pause state changes so Unity's internal PhysX step and FixedUpdate loop
    ///    remain at 50 Hz real-time instead of executing 800 times/sec at 16x speed (even while paused).
    /// 2. Pause-gates TargetLine.FixedUpdate, StarSystem.Update(0.0), WorkManager.Update, AudioATC.Update,
    ///    and idle Electrical.Update so background world/ship loops do not burn CPU while paused.
    /// 3. Decouples UI/device polling and animation timers (GUIBreaker, GUIOutput, GUICrewNoise, GUINoise,
    ///    WorkManager, GUIComputerReadout, GUILamp, GUILedMeter, GUI7Seg) from Time.deltaTime to
    ///    Time.unscaledDeltaTime so 16x simulation speed does not make UI panels poll 16x faster.
    /// 4. Caches Sprite instances in GUIOutput.Update and GUIOutput.LoadFromConnection instead of
    ///    calling Sprite.Create on every poll tick.
    /// 5. Fixes Rotor.Animate multiplying by Time.timeScale twice (Time.deltaTime * Time.timeScale = 256x at 16x).
    /// </summary>
    public sealed class PauseAndTimeScaleOptimizationModule : IFastModule
    {
        public string ModuleName => "PauseAndTimeScaleOptimization";
        public bool IsEnabled => _enabledConfig != null && _enabledConfig.Value;

        private ConfigEntry<bool> _enabledConfig;
        private static ManualLogSource _logger;

        private static AccessTools.FieldRef<Electrical, bool> _electricalIsInitialisingRef;
        private static AccessTools.FieldRef<Electrical, bool> _electricalNeedsCheckRef;

        private static readonly Dictionary<Texture2D, Sprite> _guiOutputSpriteCache = new Dictionary<Texture2D, Sprite>(128);

        public void Initialize(ConfigFile config, ManualLogSource logger, Harmony harmony)
        {
            _logger = logger;
            _enabledConfig = config.Bind(
                "07. Pause & TimeScale",
                "EnablePauseAndTimeScaleOptimization",
                true,
                "Fixes high CPU usage when paused at 16x speed, keeps FixedUpdate at 50Hz real-time, pause-gates idle world loops, decouples UI timers to unscaled time, and caches GUIOutput sprites.");

            if (!IsEnabled)
            {
                return;
            }

            PatchTimeScaleFixedDeltaSync(harmony);
            PatchPauseGatedUpdates(harmony);
            PatchUnscaledUiTimersAndSprites(harmony);
            PatchRotorDoubleTimeScale(harmony);
        }

        private void PatchTimeScaleFixedDeltaSync(Harmony harmony)
        {
            try
            {
                HarmonyMethod syncPostfix = new HarmonyMethod(
                    typeof(PauseAndTimeScaleOptimizationModule),
                    nameof(SyncFixedDeltaTime_Postfix));

                MethodInfo resetTimeScale = AccessTools.Method(typeof(CrewSim), nameof(CrewSim.ResetTimeScale));
                if (resetTimeScale != null)
                {
                    harmony.Patch(resetTimeScale, postfix: syncPostfix);
                }

                MethodInfo timeScaleMult = AccessTools.Method(typeof(CrewSim), nameof(CrewSim.TimeScaleMult), new[] { typeof(float) });
                if (timeScaleMult != null)
                {
                    harmony.Patch(timeScaleMult, postfix: syncPostfix);
                }

                MethodInfo setPaused = AccessTools.PropertySetter(typeof(CrewSim), nameof(CrewSim.Paused));
                if (setPaused != null)
                {
                    harmony.Patch(setPaused, postfix: syncPostfix);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"[{ModuleName}] Failed to patch CrewSim TimeScale FixedDeltaTime sync: {ex.Message}");
            }

            try
            {
                MethodInfo targetLineFixedUpdate = AccessTools.Method(typeof(TargetLine), "FixedUpdate");
                if (targetLineFixedUpdate != null)
                {
                    harmony.Patch(
                        targetLineFixedUpdate,
                        prefix: new HarmonyMethod(typeof(PauseAndTimeScaleOptimizationModule), nameof(SkipWhenPaused_Prefix)));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"[{ModuleName}] Failed to patch TargetLine.FixedUpdate: {ex.Message}");
            }
        }

        private void PatchPauseGatedUpdates(Harmony harmony)
        {
            try
            {
                MethodInfo starSystemUpdate = AccessTools.Method(typeof(StarSystem), nameof(StarSystem.Update), new[] { typeof(double) });
                if (starSystemUpdate != null)
                {
                    harmony.Patch(
                        starSystemUpdate,
                        prefix: new HarmonyMethod(typeof(PauseAndTimeScaleOptimizationModule), nameof(StarSystem_Update_Prefix)));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"[{ModuleName}] Failed to patch StarSystem.Update: {ex.Message}");
            }

            try
            {
                MethodInfo workManagerUpdate = AccessTools.Method(typeof(WorkManager), "Update");
                if (workManagerUpdate != null)
                {
                    harmony.Patch(
                        workManagerUpdate,
                        prefix: new HarmonyMethod(typeof(PauseAndTimeScaleOptimizationModule), nameof(SkipWhenPaused_Prefix)),
                        transpiler: new HarmonyMethod(typeof(PauseAndTimeScaleOptimizationModule), nameof(ReplaceDeltaTimeWithPausedAwareUnscaled_Transpiler)));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"[{ModuleName}] Failed to patch WorkManager.Update: {ex.Message}");
            }

            try
            {
                MethodInfo audioAtcUpdate = AccessTools.Method(typeof(AudioATC), "Update");
                if (audioAtcUpdate != null)
                {
                    harmony.Patch(
                        audioAtcUpdate,
                        prefix: new HarmonyMethod(typeof(PauseAndTimeScaleOptimizationModule), nameof(SkipWhenPaused_Prefix)));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"[{ModuleName}] Failed to patch AudioATC.Update: {ex.Message}");
            }

            try
            {
                _electricalIsInitialisingRef = AccessTools.FieldRefAccess<Electrical, bool>("isInitialising");
                _electricalNeedsCheckRef = AccessTools.FieldRefAccess<Electrical, bool>("bNeedsCheck");

                MethodInfo electricalUpdate = AccessTools.Method(typeof(Electrical), "Update");
                if (electricalUpdate != null && _electricalIsInitialisingRef != null && _electricalNeedsCheckRef != null)
                {
                    harmony.Patch(
                        electricalUpdate,
                        prefix: new HarmonyMethod(typeof(PauseAndTimeScaleOptimizationModule), nameof(Electrical_Update_Prefix)));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"[{ModuleName}] Failed to patch Electrical.Update: {ex.Message}");
            }
        }

        private void PatchUnscaledUiTimersAndSprites(Harmony harmony)
        {
            HarmonyMethod pausedAwareTranspiler = new HarmonyMethod(
                typeof(PauseAndTimeScaleOptimizationModule),
                nameof(ReplaceDeltaTimeWithPausedAwareUnscaled_Transpiler));

            Type[] pausedAwareUiTypes =
            {
                typeof(GUIBreaker),
                typeof(GUICrewNoise),
                typeof(GUINoise)
            };

            for (int i = 0; i < pausedAwareUiTypes.Length; i++)
            {
                Type uiType = pausedAwareUiTypes[i];
                try
                {
                    MethodInfo updateMethod = AccessTools.Method(uiType, "Update");
                    if (updateMethod != null)
                    {
                        harmony.Patch(updateMethod, transpiler: pausedAwareTranspiler);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"[{ModuleName}] Failed to patch {uiType.Name}.Update: {ex.Message}");
                }
            }

            try
            {
                HarmonyMethod guiOutputTranspiler = new HarmonyMethod(
                    typeof(PauseAndTimeScaleOptimizationModule),
                    nameof(GUIOutput_Transpiler));

                MethodInfo guiOutputUpdate = AccessTools.Method(typeof(GUIOutput), "Update");
                if (guiOutputUpdate != null)
                {
                    harmony.Patch(guiOutputUpdate, transpiler: guiOutputTranspiler);
                }

                MethodInfo guiOutputLoad = AccessTools.Method(typeof(GUIOutput), nameof(GUIOutput.LoadFromConnection));
                if (guiOutputLoad != null)
                {
                    harmony.Patch(guiOutputLoad, transpiler: guiOutputTranspiler);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"[{ModuleName}] Failed to patch GUIOutput: {ex.Message}");
            }

            HarmonyMethod unscaledTranspiler = new HarmonyMethod(
                typeof(PauseAndTimeScaleOptimizationModule),
                nameof(ReplaceDeltaTimeWithUnscaled_Transpiler));

            Type[] unscaledUiTypes =
            {
                typeof(GUIComputerReadout),
                typeof(GUILamp),
                typeof(GUILedMeter),
                typeof(GUI7Seg)
            };

            for (int i = 0; i < unscaledUiTypes.Length; i++)
            {
                Type uiType = unscaledUiTypes[i];
                try
                {
                    MethodInfo updateMethod = AccessTools.Method(uiType, "Update");
                    if (updateMethod != null)
                    {
                        harmony.Patch(updateMethod, transpiler: unscaledTranspiler);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"[{ModuleName}] Failed to patch {uiType.Name}.Update: {ex.Message}");
                }
            }
        }

        private void PatchRotorDoubleTimeScale(Harmony harmony)
        {
            try
            {
                MethodInfo rotorAnimate = AccessTools.Method(typeof(Rotor), "Animate");
                if (rotorAnimate != null)
                {
                    harmony.Patch(
                        rotorAnimate,
                        transpiler: new HarmonyMethod(typeof(PauseAndTimeScaleOptimizationModule), nameof(Rotor_Animate_Transpiler)));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"[{ModuleName}] Failed to patch Rotor.Animate: {ex.Message}");
            }
        }

        public static void SyncFixedDeltaTime_Postfix()
        {
            // Keep Unity's FixedUpdate / PhysX step at 50 Hz real-world time during 1x..16x acceleration
            // instead of running 800 FixedUpdate steps per real second at 16x (even when CrewSim.Paused is true).
            Time.fixedDeltaTime = 0.02f * Mathf.Clamp(Time.timeScale, 1f, 16f);
        }

        public static bool SkipWhenPaused_Prefix()
        {
            if (CrewSim.Paused)
            {
                FastStats.PausedUpdatesSkipped++;
                return false;
            }
            return true;
        }

        public static bool StarSystem_Update_Prefix(double __0)
        {
            if (__0 <= 0.0 && CrewSim.Paused)
            {
                FastStats.PausedUpdatesSkipped++;
                return false;
            }
            return true;
        }

        public static bool Electrical_Update_Prefix(Electrical __instance)
        {
            if (CrewSim.Paused &&
                _electricalIsInitialisingRef != null &&
                _electricalNeedsCheckRef != null &&
                !_electricalIsInitialisingRef(__instance) &&
                !_electricalNeedsCheckRef(__instance))
            {
                FastStats.PausedUpdatesSkipped++;
                return false;
            }
            return true;
        }

        public static float GetUiPollDeltaTime()
        {
            if (CrewSim.Paused)
            {
                FastStats.PausedUpdatesSkipped++;
                return 0f;
            }

            FastStats.UnscaledUiTimersDecoupled++;
            return Time.unscaledDeltaTime;
        }

        public static float GetUnscaledDeltaTime()
        {
            FastStats.UnscaledUiTimersDecoupled++;
            return Time.unscaledDeltaTime;
        }

        public static Sprite GetOrCreateCachedSprite(Texture2D tex, Rect rect, Vector2 pivot)
        {
            if (tex == null)
            {
                return null;
            }

            if (_guiOutputSpriteCache.TryGetValue(tex, out Sprite cached) && cached != null)
            {
                FastStats.GuiOutputSpritesCached++;
                return cached;
            }

            Sprite created = Sprite.Create(tex, rect, pivot);
            _guiOutputSpriteCache[tex] = created;
            return created;
        }

        public static IEnumerable<CodeInstruction> ReplaceDeltaTimeWithPausedAwareUnscaled_Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo getDeltaTime = AccessTools.PropertyGetter(typeof(Time), nameof(Time.deltaTime));
            MethodInfo getUiPollDelta = AccessTools.Method(typeof(PauseAndTimeScaleOptimizationModule), nameof(GetUiPollDeltaTime));

            foreach (CodeInstruction inst in instructions)
            {
                if (getDeltaTime != null && getUiPollDelta != null && inst.Calls(getDeltaTime))
                {
                    yield return new CodeInstruction(OpCodes.Call, getUiPollDelta).WithLabels(inst.labels);
                    continue;
                }

                yield return inst;
            }
        }

        public static IEnumerable<CodeInstruction> ReplaceDeltaTimeWithUnscaled_Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo getDeltaTime = AccessTools.PropertyGetter(typeof(Time), nameof(Time.deltaTime));
            MethodInfo getUnscaledDelta = AccessTools.Method(typeof(PauseAndTimeScaleOptimizationModule), nameof(GetUnscaledDeltaTime));

            foreach (CodeInstruction inst in instructions)
            {
                if (getDeltaTime != null && getUnscaledDelta != null && inst.Calls(getDeltaTime))
                {
                    yield return new CodeInstruction(OpCodes.Call, getUnscaledDelta).WithLabels(inst.labels);
                    continue;
                }

                yield return inst;
            }
        }

        public static IEnumerable<CodeInstruction> GUIOutput_Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo getDeltaTime = AccessTools.PropertyGetter(typeof(Time), nameof(Time.deltaTime));
            MethodInfo getUiPollDelta = AccessTools.Method(typeof(PauseAndTimeScaleOptimizationModule), nameof(GetUiPollDeltaTime));

            MethodInfo spriteCreate = AccessTools.Method(
                typeof(Sprite),
                nameof(Sprite.Create),
                new[] { typeof(Texture2D), typeof(Rect), typeof(Vector2) });
            MethodInfo getOrCreateCachedSprite = AccessTools.Method(
                typeof(PauseAndTimeScaleOptimizationModule),
                nameof(GetOrCreateCachedSprite));

            foreach (CodeInstruction inst in instructions)
            {
                if (getDeltaTime != null && getUiPollDelta != null && inst.Calls(getDeltaTime))
                {
                    yield return new CodeInstruction(OpCodes.Call, getUiPollDelta).WithLabels(inst.labels);
                    continue;
                }

                if (spriteCreate != null && getOrCreateCachedSprite != null && inst.Calls(spriteCreate))
                {
                    yield return new CodeInstruction(OpCodes.Call, getOrCreateCachedSprite).WithLabels(inst.labels);
                    continue;
                }

                yield return inst;
            }
        }

        public static IEnumerable<CodeInstruction> Rotor_Animate_Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo getTimeScale = AccessTools.PropertyGetter(typeof(Time), nameof(Time.timeScale));

            foreach (CodeInstruction inst in instructions)
            {
                if (getTimeScale != null && inst.Calls(getTimeScale))
                {
                    yield return new CodeInstruction(OpCodes.Ldc_R4, 1f).WithLabels(inst.labels);
                    continue;
                }

                yield return inst;
            }
        }
    }
}
