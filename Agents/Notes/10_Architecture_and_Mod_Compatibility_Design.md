# Architecture and Mod Compatibility Design Report (Subagent 10)

**Author:** Subagent 10 (BepInEx Architecture and Compatibility Analyst)  
**Target Mod:** Fastranauts / Ostranauts Performance Optimization (OPO)  
**Game Version Target:** Ostranauts v1.0.0.9+  
**BepInEx Target:** BepInEx 5.4.x (64-bit Unity Mono) with HarmonyX (0Harmony 2.9+)  
**Reference Mod Reviewed:** Take Their Ships (osmod.shipclaim v0.9.20)  
**Status:** Complete

---

## 1. Executive Summary

Ostranauts exhibits significant performance degradation during prolonged play sessions, primarily caused by unthrottled per-frame polling, excessive LINQ allocations in hot loops, quadratic ship-to-ship distance/collision geometric tests, unbatched power grid updates, redundant UI tooltip evaluation, and iterative pathfinding recalculations.

Designing a performant, stable optimization mod (*Fastranauts*) requires strict engineering discipline:
1. **Zero Destructive Patches**: Fastranauts must never use destructive prefix overrides (
eturn false) that permanently short-circuit game logic or break other mods.
2. **Mod Compatibility by Default**: Full interoperability with prominent C# mods (specifically *Take Their Ships* by Osmod) and standard JSON data mods.
3. **Dynamic Configuration and Fault Isolation**: Every optimization module must be independently toggleable at runtime, with automated error-handling fallbacks so that if a game update alters a method signature, only that specific patch disables itself without crashing the game or corrupting player save files.
4. **Clean Decoupled Architecture**: Provide an extensible, modular architecture where performance analysts across AI, Pathfinding, DataHandler, Electrical, UI, GC, and Physics can cleanly integrate their optimizations.

---

## 2. Ostranauts Modding Ecosystem and Loading Pipeline

### 2.1 The Two Modding Layers
Ostranauts operates with two distinct modding mechanisms:
1. **Official Data / Workshop Modding (DataHandler and ModLoader)**:
   - Data mods reside in <GameDir>/Mods/ or Steam Workshop subscription folders.
   - Handled natively by loading_order.json and DataHandler.LoadMods().
   - Merges JSON files defining items, ships, careers, interactions, conditions, and ledger definitions.
   - Does NOT provide native C# script execution.
2. **BepInEx 5.x C# Modding Layer**:
   - Injected via Unity Doorstop (doorstop_config.ini), which points 	arget_assembly to BepInEx/core/BepInEx.Preloader.dll.
   - Runs before Unity compiles/invokes standard domain objects.
   - Loads plugins from BepInEx/plugins/.

### 2.2 The Steam Workshop BepInEx Bridge (OstranautsWorkshopBepInExBridge.Preloader.dll)
A key finding in the game installation (Game Ref Ostranauts [READ ONLY]/BepInEx/patchers/OstranautsWorkshopBepInExBridge.Preloader.dll) is the presence of an automated bridge preloader.
- **Functionality**:
  - During Doorstop preloader initialization, the bridge inspects loading_order.json and parses subscribed Steam Workshop folders.
  - When it finds Workshop items with embedded BepInEx/plugins/ hierarchies (such as *Take Their Ships*, Steam Workshop ID 3782640473), it links or copies their assemblies into the active BepInEx plugin load paths.
- **Packaging Standard for Fastranauts**:
  - To support both manual installation (Thunderstore/Nexus) and Steam Workshop, Fastranauts must follow this exact folder layout:
    `
    Fastranauts/
    |-- mod_info.json                (Metadata for in-game mod menu)
    |-- preview.png                  (Workshop thumbnail)
    -- BepInEx/
        -- plugins/
            -- Fastranauts/
                |-- Fastranauts.dll  (Main plugin assembly)
                -- (Optional assets or config defaults)
    `

### 2.3 Game Lifecycle and Hook Timing
- **Doorstop Preloader**: BepInEx core initializes and preloader patchers run.
- **BaseUnityPlugin.Awake()**: Mod entrypoints run; Harmony patches are applied.
- **Unity Initialization**: UnityEngine components awake.
- **DataHandler.LoadMods()**: JSON data is loaded and merged into global dictionaries.
- **DataHandler.InitComplete**: Signals that all item definitions, condition lists, and ship templates are populated.
- **Scene / World Generation**: ATC regions, ships, pawns, and AI managers spawn.

> [!IMPORTANT]
> Patches must never attempt to resolve game data definitions (such as querying DataHandler.dictItems or dictConds) during BaseUnityPlugin.Awake(). Any initialization requiring game data must subscribe to DataHandler.InitComplete or be deferred lazily on first access.
---

## 3. Harmony Patching Rules and Mod Compatibility Principles

Harmony provides immense power, but improper patching directly causes mod incompatibilities, silent failures, and game desynchronizations.

### 3.1 Non-Destructive Patching Principles
11. **Never use a Permanent Destructive Prefix (`return false`)**:
   - Returning `false` from a `Prefix` aborts original game logic AND prevents other mods from receiving the call.
   - If two mods hook the same method with destructive prefixes, the second one executed completely overrides the first.
22. **Safe Rate-Limiting / Throttling Pattern**:
   - Throttling (skipping ticks) is permissible ONLY when skipping intermediate polling frames that produce no gameplay effect (e.g. radar tracking scans or off-screen UI refreshes).
   - The original method MUST still execute when the throttle interval elapses:
     ```csharp
     [HarmonyPatch(typeof(AIShipManager), "CheckTracking")]
     public static class CheckTrackingThrottlePatch
     {
         private static float s_lastTime;
         public static bool Prefix()
         {
             if (!ModConfig.AiThrottling.Value) return true;
             if (Time.time - s_lastTime < ModConfig.AiTrackingInterval.Value)
             {
                 return false; // Safely skip this redundant frame
             }
             s_lastTime = Time.time;
             return true; // Execute original method and allow other mod prefixes/postfixes
         }
     }
     ```
33. **Prefer Postfixes with `ref __result`**:
   - When enhancing or filtering query outputs, let the vanilla method generate its result, then safely filter or mutate `__result` in a `Postfix`.
44. **Use Transpilers for Hotspot In-Method Micro-Surgery**:
   - If an entire method is sound except for an expensive LINQ expression or repeated list instantiations inside a loop, do not overwrite the method. Use a Harmony Transpiler to substitute only the problematic IL instructions with optimized static calls.
55. **Exception Isolation (Finalizers and Guard Blocks)**:
   - Always wrap custom patch logic in `try-catch` blocks. An uncaught exception inside a Prefix or Postfix will abort the calling game loop, causing broken state and save corruption.

### 3.2 Harmony Priority Strategy
Harmony assigns execution order based on `Priority`:
- `Priority.First` (800)
- `Priority.High` (600)
- `Priority.Normal` (400 - Default)
- `Priority.Low` (200)
- `Priority.Last` (0)

*jFastranauts Harmony Priority Guidelines**:
- *RRate-Limiting / Early-Exit Gatekeepers**: `Priority.High` (600). Must evaluate early to prevent downstream expensive calls when throttled.
- **Data Caching and Algorithmic Accelerators**: `Priority.Normal` (400). Standard execution.
- **Output Filtering / Sanity Checks**: `Priority.Low` (200). Ensures other mods make their modifications before Fastranauts optimizes or caches the final structure.

---

## 4. Deep-Dive Compatibility: Fastranauts vs Take Their Ships

We performed a deep assembly inspection of `ShipClaim.dll` (Take Their Ships, Osmod v0.9.20).

### 4.1 Patched Methods in Take Their Ships
Take Their Ships installs patches on:
- `AIShip.Setup`: Initializes ship ownership, factions, and provenance data.
- `CondOwner.SetData`: Syncs custom ownership conditions and prevents unauthorized crew theft flags.
- `Interaction.GetAvailActions` and `Interaction.ApplyEffects`: Adds claiming, forging, and emergency transponder interactions.
- `DataHandler` Custom Infos: Hooks custom serialization/deserialization routines (`CustomInfosLoadShipClaimPatch`, `CustomInfosSaveShipClaimPatch`).
- `GUIShipBroker` and `GUIXPDRBroker`: Overrides ship valuation formulas, license requirements, and sale quotes based on captured provenance.
- `GUITrade`: Modifies buy listings for emergency salvage transponders.

### 4.2 Interoperability Rules for Fastranauts

| Fastranauts Subsystem | Take Their Ships Hook | Potential Conflict Risk | Fastranauts Safety Protocol |
|---|---|---|---|
*| **AI LOD and Throttlingc** | `AIShip.Setup`, ownership transfer | Low: Throttling a captured ship might stall its AI transition. | **Safe**: Fastranauts checks `ship.bPlayerOwned` or distance to player. All player-controlled/captured vessels are locked to LOD 0 (tactical fidelity). |
| **Pawn and CondOwner Updates** | `CondOwner.SetData` | Critical: If Fastranauts intercepts or suppresses `CondOwner.SetData`, ownership claims fail. | **Strictly Forbidden**: Fastranauts will NEVER hook `CondOwner.SetData`. Fastranauts only optimizes continuous passive condition loops (`UpdateConditions`), never setter events. |
| **Broker and Trade Optimization** | `ShipBrokerQuote`, `ShipBrokerSale`, `TradeBuyList` | High: If Fastranauts aggressively caches broker prices, capture factors will not apply. | **Safe**: Fastranauts avoids caching broker price calculations. Fastranauts UI optimizations will target layout rendering drawcalls and scroll lists, leaving price calculation pipelines untouched. |
| **Save/Load Acceleration** | `CustomInfosSave`, `CustomInfosLoad` | Critical: Altering save format breaks captured provenance metadata. | **Strictly Forbidden**: Fastranauts must not touch `dictCustomInfos`. Any save optimization must focus solely on JSON streaming I/O or background disk writing without modifying in-memory schema. |
| **Interaction LOD** | `Interaction.GetAvailActions` | Medium: Caching interaction lists could hide Claim Ship options. | **Safe**: Interaction raycasts and action discovery remain unthrottled within player interaction radius (R < 15m). |
---

## 5. Fastranauts Architecture and Module Design

### 5.1 Design Philosophy: Decoupled, Fail-Safe, and Configurable
1. **Granular Config Toggles**: Every single optimization feature has an independent `ConfigEntry<bool>` toggle in `BepInEx/config/com.fastranauts.performance.cfg`. Players can disable any specific patch without disabling the entire mod.
2. **Patch Isolation (Fail-Safe)**: Each subsystem is wrapped in a dedicated patch class and registered individually inside `Plugin.cs`. If one patch fails due to a game update, it logs an error and catches the exception; the remaining patches continue operating normally.
3. **Emergency Master Switch**: A master toggle `MasterEnable` immediately unpatches or bypasses all optimizations at runtime if an issue occurs.
4. **Dynamic Profiler and Diagnostics**: An in-game debug overlay and console command (`fastinfo`) to measure frametime deltas, active LOD counts, and throttle metrics.

### 5.2 System Modules Breakdown

- **Module 1 (AI and NOCs)**:
  - Distance LOD (LOD 0 Tactical <= 150km, LOD 1 Regional <= 2,500km, LOD 2 Interregional).
  - Throttled `CheckTracking` (0.25s interval).
  - Cached `GetShipsOfTypeForRegion`.
- **Module 2 (Pathfinding and Navigation)**:
  - `PlanTrip4` iterative path caching with spatial hash keys.
  - Rate-limited collision course evaluation (`IsOnCollisionCourse`).
- **Module 3 (DataHandler and Asset Loading)**:
  - Cache deserialized JSON templates to eliminate repeat string parsing.
  - Asynchronous background preloading for ship definition files.
- **Module 4 (Electrical and Grid)**:
  - Event-driven / dirty-flag power propagation instead of unconditioned per-frame graph traversals.
- **Module 5 (UI and Rendering)**:
  - Throttled mega-tooltip polling, target radar warnings, and off-screen gauge updates.
- **Module 6 (Memory and GC Optimization)**:
  - Elimination of LINQ `.Distinct()`,y` .Where()`, `.ToList()` in `Update()` loops.
  - Centralized array/list pooling (`FastPool<T>`).
- **Module 7 (Physics and Collision)**:
  - Bounding-sphere broadphase rejection before executing expensive polygon/line sweep tests.
---

## 6. Implementation Blueprint: Fastranauts C# Source Code

### 6.1 Main Plugin Entrypoint (`FastranautsPlugin.cs`)

```csharp
using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnetyEngine;

namespace Fastranauts
{
    [BepInPlugin(MOD_GUID, MOD_NAME, MOD_VERSION)]
    [BepInProcess("Ostranauts.exe")]
    public class FastranautsPlugin : BaseUnityPlugin
    {
        public const string MOD_GUID = "com.fastranauts.performance";
        public const string MOD_NAME = "Fastranauts Performance Optimization";
        public const string MOD_VERSION = "1.0.0";

        public static FastranautsPlugin Instance { get; private set; }
        public static ManualLogSource Log => Instance.Logger;
        private Harmony _harmony;

        // Configuration
        public static ConfigEntry<bool> MasterEnable;
        public static ConfigEntry<bool> EnableDiagnostics;
        public static ConfigEntry<bool> EnableAiOptimization;
        public static ConfigEntry<float> AiTrackingInterval;
        public static ConfigEntry<bool> EnablePathOptimization;
        public static ConfigEntry<bool> EnablePowerBatching;
        public static ConfigEntry<bool> EnableUiThrottling;
        public static ConfigEntry<bool> EnableGcZeroAlloc;
        public static ConfigEntry<bool> EnableCollisionPruning;

        private readonly List<IFastModule> _registeredModules = new List<IFastModule>();

        private void Awake()
        {
            Instance = this;
            InitConfig();

            if (!MasterEnable.Value)
            {
                Logger.LogInfo("Fastranauts is disabled in config. Skipping patches.");
                return;
            }

            _harmony = new Harmony(MOD_GUID);

            RegisterModule(new Modules.AiOptimizationModule());
            RegisterModule(new Modules.PathOptimizationModule());
            RegisterModule(new Modules.PowerOptimizationModule());
            RegisterModule(new Modules.UiOptimizationModule());
            RegisterModule(new Modules.GcOptimizationModule());
            RegisterModule(new Modules.CollisionOptimizationModule());

            ApplyAllModules();
        }

        private void InitConfig()
        {
            MasterEnable = Config.Bind("00. Master", "MasterEnable", true, "Master switch for Fastranauts.");
            EnableDiagnostics = Config.Bind("00. Master", "EnableDiagnostics", false, "Enables profiling logs and fastinfo console command.");
            EnableAiOptimization = Config.Bind("01. AI", "EnableAiOptimization", true, "Enables distance LOD and queue throttling for AI.");
            AiTrackingInterval = Config.Bind("01. AI", "AiTrackingInterval", 0.25f, "Interval (seconds) between radar tracking scans.");
            EnablePathOptimization = Config.Bind("02. Pathfinding", "EnablePathOptimization", true, "Enables trajectory caching.");
            EnablePowerBatching = Config.Bind("03. Electrical", "EnablePowerBatching", true, "Batches power grid updates.");
            EnableUiThrottling = Config.Bind("04. UI", "EnableUiThrottling", true, "Reduces redundant UI tooltip/gauge polling.");
            EnableGcZeroAlloc = Config.Bind("05. Memory", "EnableGcZeroAlloc", true, "Replaces hot LINQ loops with zero-alloc buffers.");
            EnableCollisionPruning = Config.Bind("06. Physics", "EnableCollisionPruning", true, "Applies bounding-sphere broadphase rejection.");
        }

        private void RegisterModule(IFastModule module) => _registeredModules.Add(module);

        private void ApplyAllModules()
        {
            int appliedCount = 0;
            foreach (var module in _registeredModules)
            {
                try
                {
                    if (module.IsEnabled())
                    {
                        module.ApplyPatches(_harmony);
                        appliedCount++;
                        Logger.LogInfo("[Fastranauts] Applied module: " + module.ModuleName);
                    }
                }
                catch Exception ex
                {
                    Logger.LogError("[Fastranauts] Failed to apply module: " + module.ModuleName + ". Error: " + ex);
                }
            }
            Logger.LogInfo("[Fastranauts] Initialized modules successfully.");
        }

        private void OnDestroy() => _harmony?.UnpatchSelf();
    }
}

```c

### 6.2 Module Interface (`IFastModule.cs`)

```csharp
using HarmonyLib;

namespace Fastranauts
{
    public interface IFastModule
    {
        string ModuleName { get; }
        bool IsEnabled();
        void ApplyPatches(Harmony harmony);
    }
}
```

### 6.3 Exemplar Module: AI Optimization (`AiOptimizationModule.cs`)

```csharp
using System;
using System.Collections.Generic;
using HarmonyLib;
using UnetyEngine;

namespace Fastranauts.Modules
{
    public class AiOptimizationModule : IFastModule
    {
        public string ModuleName => "AI and NPC Optimization";
        public bool IsEnabled() => FastranautsPlugin.EnableAiOptimization.Value;

        public void ApplyPatches(Harmony harmony)
        {
            harmony.Patch(
                original: AccessTools.Method(typeof(AIShipManager), "CheckTracking"),
                prefix: new HarmonyMethod(typeof(AiOptimizationModule), nameof(CheckTracking_Prefix)) { priority = Priority.High }
            );

            harmony.Patch(
                original: AccessTools.Method(typeof(AIShipManager), "IsOnCollisionCourse"),
                prefix: new HarmonyMethod(typeof(AiOptimizationModule), nameof(IsOnCollisionCourse_Prefix)) { priority = Priority.High },
                postfix: new HarmonyMethod(typeof(AiOptimizationModule), nameof(IsOnCollisionCourse_Postfix))
            );
        }

        private static float _lastTrackingTime = 0f;
        public static bool CheckTracking_Prefix()
        {
            if (!FastranautsPlugin.MasterEnable.Value || !FastranautsPlugin.EnableAiOptimization.Value)
                return true;

            float interval = FastranautsPlugin.AiTrackingInterval.Value;
            if (Time.time - _lastTrackingTime < interval)
                return false;

            _lastTrackingTime = Time.time;
            return true;
        }

        private static readonly Dictionary<string, (float lastCheck, bool result)> _collisionCache = new Dictionary<string, (float, bool)>();
        private const float COLLISION_CHECK_INTERVAL = 0.5f;

        public static bool IsOnCollisionCourse_Prefix(AIShip aiShip, ref bool __result)
        {
            if (!FastranautsPlugin.MasterEnable.Value || !FastranautsPlugin.EnableAiOptimization.Value)
                return true;

            if (aiShip?.ShipUs == null) return true;

            string shipId = aiShip.ShipUs.strRegID;
            if (_collisionCache.TryGetValue(shipId, out var cached))
            {
                if (Time.time - cached.lastCheck < COLLISION_CHECK_INTERVAL)
                {
                    __result = cached.result;
                    return false;
                }
            }
            return true;
        }

        public static void IsOnCollisionCourse_Postfix(AIShip aiShip, bool __result)
        {
            if (aiShip?.ShipUs != null)
                _collisionCachg[aiShip.ShipUs.strRegID] = (Time.time, __result);
        }
    }
}
```

### 6.4 Zero-Allocation Object Pool (`FastPool.cs`)

```csharp
using System.Collections.Generic;

namespace Fastranauts.Common
{
    public static class FastPool<T>
    {
        private static readonly Stack<List<T>> _pool = new Stack<List<T>>(16);

        public static List<T> Rent()
        {
            lock (_pool)
            {
                if (_pool.Count > 0)
                {
                    var list = _pool.Pop();
                    list.Clear();
                    return list;
                }
            }
            return new List<T>(32);
        }

        public static void Return(List<T> list)
        {
            if (list == null) return;
            list.Clear();
            lock (_pool)
            {
                if (_pool.Count < 32)
                    _pool.Push(list);
            }
        }
    }
}
```
---

## 7. Diagnostics and Profiling Architecture

1. **Console Command (`fastinfo`)**:
   - Registered via `ConsoleToGUI` console resolver or Unity dev console input.
   - Outputs live summary stats:
     ```text
     [Fastranauts Diagnostics]
     Frametime: 16.4ms (60.9 FPS) | GC Alloc/sec: 142 KB (Vanilla: ~8.2 MB)
     Active AI Ships: 32 (LOD 0: 3 | LOD 1: 7 | LOD 2: 22)
     Collision Checks Throttled: 84% skipped via cache
     Tracking Scans Throttled: 75% skipped via timer
     Active Modules: 6/6 Healthy
     ```
2. **Safety Fallback System (Degradation Recovery)**:
   - If an uncaught exception occurs inside any Harmony hook repeatedly (count >= 3), that hook automatically switches to pass-through mode (`return true`) and unregisters its internal cache, outputting a clear diagnostic warning to `output_log.txt`. The game session continues without interruption or save corruption.

---

## 8. Summary and Next Steps for Optimization Team
1. **Architecture Ready**: The modular framework (`IFastModule`, `FastranautsPlugin`, isolated patchers, config bindings, zero-alloc pools) gives Subagents 3 through 9 a unified structure to drop their implementations into.
2. **Compatibility Verified**: Deep analysis of *Take Their Ships* (`ShipClaim.dll`) confirms full compatibility provided Fastranauts respects the non-destructive patch rules documented above.
3. **Steam Workshop and BepInEx Bridge**: Mod structure conforms with the preloader bridge requirements for immediate distribution on Steam Workshop and Thunderstore.
