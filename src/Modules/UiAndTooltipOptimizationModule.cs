using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using BepInEx.Configuration;
using BepInEx.Logging;
using Fastranauts.Common;
using HarmonyLib;
using Ostranauts.InputControl;
using Ostranauts.Inventory;
using UnityEngine;
using UnityEngine.EventSystems;

namespace Fastranauts.Modules
{
    /// <summary>
    /// Fixes Bottlenecks #4 &amp; #6 from the runtime profiler and UI analysis:
    /// - GUIInventoryWindow::UpdateWindow calling Redraw() on Ground windows every single frame (25 tile LOS raycasts at 60–144 Hz).
    /// - ItemList &gt; GUIItemList::Update rebuilding hover tooltip &amp; pressure strings every frame even when the mouse is stationary.
    ///
    /// Mod-Safe Design:
    /// - Replaces only the per-frame polling call to this.Redraw() inside GUIInventoryWindow.UpdateWindow via Transpiler,
    ///   leaving direct calls to GUIInventoryWindow.Redraw() (from item drag-and-drop or other mods) 100% immediate.
    /// - Runs GUIItemList.Update() immediately whenever the mouse moves, UI hover state changes, or a key is pressed,
    ///   and throttles to 10 Hz only while the cursor is stationary.
    /// </summary>
    public sealed class UiAndTooltipOptimizationModule : IFastModule
    {
        public string ModuleName => "UiAndTooltipOptimization";
        public bool IsEnabled => _enabledConfig != null && _enabledConfig.Value;

        private ConfigEntry<bool> _enabledConfig;
        private ConfigEntry<float> _groundRedrawIntervalConfig;
        private ConfigEntry<float> _stationaryTooltipIntervalConfig;
        private static ManualLogSource _logger;

        private static float _groundRedrawInterval = 0.20f;
        private static float _stationaryTooltipInterval = 0.10f;

        private static float _lastGroundRedrawTime;
        private static Vector3 _lastGroundDollPos = new Vector3(float.MinValue, float.MinValue, float.MinValue);
        private static GUIInventoryWindow _lastGroundWindow;

        private static float _lastTooltipUpdateTime;
        private static Vector2 _lastMousePos = new Vector2(float.MinValue, float.MinValue);
        private static bool _lastPointerOverUi;

        public void Initialize(ConfigFile config, ManualLogSource logger, Harmony harmony)
        {
            _logger = logger;
            _enabledConfig = config.Bind(
                "05. UI & Inventory",
                "EnableUiAndTooltipOptimization",
                true,
                "Throttles redundant per-frame Ground inventory 25-tile LOS scans and stationary cursor tooltip string rebuilds.");

            _groundRedrawIntervalConfig = config.Bind(
                "05. UI & Inventory",
                "GroundInventoryPollIntervalSeconds",
                0.20f,
                new ConfigDescription(
                    "Minimum interval (seconds) between automatic per-frame Ground inventory scans when the player has not moved. Direct drag/drop redraws are always instant.",
                    new AcceptableValueRange<float>(0.05f, 1.0f)));

            _stationaryTooltipIntervalConfig = config.Bind(
                "05. UI & Inventory",
                "StationaryTooltipRefreshIntervalSeconds",
                0.10f,
                new ConfigDescription(
                    "Refresh interval (seconds) for GUIItemList hover tooltip strings when the mouse cursor is completely stationary.",
                    new AcceptableValueRange<float>(0.05f, 0.50f)));

            if (!IsEnabled)
            {
                return;
            }

            _groundRedrawInterval = _groundRedrawIntervalConfig.Value;
            _stationaryTooltipInterval = _stationaryTooltipIntervalConfig.Value;

            MethodInfo updateWindowMethod = AccessTools.Method(
                typeof(GUIInventoryWindow),
                nameof(GUIInventoryWindow.UpdateWindow),
                new[] { typeof(CondOwner) });
            if (updateWindowMethod != null)
            {
                harmony.Patch(
                    updateWindowMethod,
                    transpiler: new HarmonyMethod(typeof(UiAndTooltipOptimizationModule), nameof(GUIInventoryWindow_UpdateWindow_Transpiler)));
            }

            MethodInfo guiItemListUpdate = AccessTools.Method(typeof(GUIItemList), "Update");
            if (guiItemListUpdate != null)
            {
                harmony.Patch(
                    guiItemListUpdate,
                    prefix: new HarmonyMethod(typeof(UiAndTooltipOptimizationModule), nameof(GUIItemList_Update_Prefix)));
            }
        }

        public static void RedrawGroundWindowThrottled(GUIInventoryWindow window, CondOwner coDoll)
        {
            if (window == null)
            {
                return;
            }

            float now = Time.unscaledTime;
            Vector3 currentPos = (coDoll != null && coDoll.tf != null) ? coDoll.tf.position : Vector3.zero;

            bool windowChanged = !ReferenceEquals(window, _lastGroundWindow);
            bool playerMoved = (currentPos - _lastGroundDollPos).sqrMagnitude > 0.01f;
            bool intervalElapsed = (now - _lastGroundRedrawTime) >= _groundRedrawInterval;

            if (windowChanged || playerMoved || intervalElapsed)
            {
                _lastGroundWindow = window;
                _lastGroundDollPos = currentPos;
                _lastGroundRedrawTime = now;
                window.Redraw();
            }
            else
            {
                FastStats.GroundInventoryRedrawsThrottled++;
            }
        }

        public static IEnumerable<CodeInstruction> GUIInventoryWindow_UpdateWindow_Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo redrawMethod = AccessTools.Method(typeof(GUIInventoryWindow), nameof(GUIInventoryWindow.Redraw));
            MethodInfo throttledHelper = AccessTools.Method(typeof(UiAndTooltipOptimizationModule), nameof(RedrawGroundWindowThrottled));

            foreach (CodeInstruction inst in instructions)
            {
                if (redrawMethod != null && throttledHelper != null && inst.Calls(redrawMethod))
                {
                    // Push arg 1 (CondOwner CODoll) onto the stack after arg 0 (this), then call RedrawGroundWindowThrottled
                    yield return new CodeInstruction(OpCodes.Ldarg_1).WithLabels(inst.labels);
                    yield return new CodeInstruction(OpCodes.Call, throttledHelper);
                    continue;
                }
                yield return inst;
            }
        }

        public static bool GUIItemList_Update_Prefix()
        {
            if (!DataHandler.bLoaded)
            {
                return true;
            }

            Vector2 mousePos = InputManager.MousePosition;
            bool pointerOverUi = EventSystem.current != null && EventSystem.current.IsPointerOverGameObject();
            float now = Time.unscaledTime;

            // Always run immediately if the mouse moved, pointer-over-UI state changed, any key was pressed, or timer elapsed
            if ((mousePos - _lastMousePos).sqrMagnitude > 0.25f ||
                pointerOverUi != _lastPointerOverUi ||
                Input.anyKeyDown ||
                (now - _lastTooltipUpdateTime) >= _stationaryTooltipInterval)
            {
                _lastMousePos = mousePos;
                _lastPointerOverUi = pointerOverUi;
                _lastTooltipUpdateTime = now;
                return true;
            }

            FastStats.TooltipUpdatesThrottled++;
            return false;
        }
    }
}
