using System.Text;

namespace Fastranauts.Common
{
    /// <summary>
    /// Tracks live telemetry counters across Fastranauts optimization modules for diagnostic reporting.
    /// </summary>
    public static class FastStats
    {
        public static long PdaForcedOverlaySkips;
        public static long PdaZeroAllocOverlayQueries;
        public static long BlankTriggerWrapperBypassed;
        public static long SensorZeroAllocListsUsed;
        public static long GasAddMolsCondAllocSaved;
        public static long NonAllocRaycastsExecuted;
        public static long RecentlyTriedCleanupsOptimized;
        public static long ThresholdSubstringsCached;
        public static long VisibilityMeshesZeroAlloc;
        public static long GroundInventoryRedrawsThrottled;
        public static long TooltipUpdatesThrottled;
        public static long JpsGridsReused;
        public static long RadarTrackingChecksThrottled;
        public static long PausedUpdatesSkipped;
        public static long PdaPausedOrDefaultSkips;
        public static long UnscaledUiTimersDecoupled;
        public static long GuiOutputSpritesCached;
        public static long SubstepVisualUpdatesDeferred;
        public static long SimComponentUpdatesThrottled;
        public static long IdleTickerSubstepsSkipped;
        public static long UndamagedStatLookupsSaved;

        public static string BuildSummaryReport()
        {
            StringBuilder sb = new StringBuilder(1024);
            sb.AppendLine("=== [Fastranauts Performance Telemetry] ===");
            sb.Append("  PDA Periodic Forced-Syncs Saved : ").AppendLine(PdaForcedOverlaySkips.ToString("N0"));
            sb.Append("  PDA Paused / Default-Mode Skips : ").AppendLine(PdaPausedOrDefaultSkips.ToString("N0"));
            sb.Append("  PDA Zero-Alloc CO Queries       : ").AppendLine(PdaZeroAllocOverlayQueries.ToString("N0"));
            sb.Append("  Blank Trigger Wrappers Bypassed : ").AppendLine(BlankTriggerWrapperBypassed.ToString("N0"));
            sb.Append("  Paused Sim/UI Updates Skipped   : ").AppendLine(PausedUpdatesSkipped.ToString("N0"));
            sb.Append("  UI Timers Decoupled (Unscaled)  : ").AppendLine(UnscaledUiTimersDecoupled.ToString("N0"));
            sb.Append("  GUIOutput Sprites Cached        : ").AppendLine(GuiOutputSpritesCached.ToString("N0"));
            sb.Append("  Idle Ticker Sub-steps Skipped   : ").AppendLine(IdleTickerSubstepsSkipped.ToString("N0"));
            sb.Append("  Sub-step Visual Updates Deferred: ").AppendLine(SubstepVisualUpdatesDeferred.ToString("N0"));
            sb.Append("  Sim Component Polls Throttled   : ").AppendLine(SimComponentUpdatesThrottled.ToString("N0"));
            sb.Append("  Undamaged Stat Lookups Saved    : ").AppendLine(UndamagedStatLookupsSaved.ToString("N0"));
            sb.Append("  Door/Pump Zero-Alloc Lists      : ").AppendLine(SensorZeroAllocListsUsed.ToString("N0"));
            sb.Append("  Gas AddGasMols Alloc Avoided    : ").AppendLine(GasAddMolsCondAllocSaved.ToString("N0"));
            sb.Append("  Physics.RaycastNonAlloc Calls   : ").AppendLine(NonAllocRaycastsExecuted.ToString("N0"));
            sb.Append("  CondOwner Cleanup Alloc Saved   : ").AppendLine(RecentlyTriedCleanupsOptimized.ToString("N0"));
            sb.Append("  Threshold Substrings Interned   : ").AppendLine(ThresholdSubstringsCached.ToString("N0"));
            sb.Append("  Light Meshes Rebuilt Zero-Alloc : ").AppendLine(VisibilityMeshesZeroAlloc.ToString("N0"));
            sb.Append("  Ground Inventory Polls Throttled: ").AppendLine(GroundInventoryRedrawsThrottled.ToString("N0"));
            sb.Append("  Hover Tooltip Rebuilds Throttled: ").AppendLine(TooltipUpdatesThrottled.ToString("N0"));
            sb.Append("  JPS Walkable Grids Reused       : ").AppendLine(JpsGridsReused.ToString("N0"));
            sb.Append("  AI Radar Tracking Polls Saved   : ").AppendLine(RadarTrackingChecksThrottled.ToString("N0"));
            sb.Append("===========================================");
            return sb.ToString();
        }
    }
}
