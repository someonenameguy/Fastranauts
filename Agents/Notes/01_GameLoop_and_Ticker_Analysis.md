# Subagent 1: Game Loop & Ticker Optimization Analysis Report

**Target Scope:** `CrewSim.cs` (`Update`, `AdvanceSim`, `UpdateICOs`, `aTickers` management, sub-stepping loop), `CondOwner.cs` (`UpdateManual`, `Cleanup`, `aTickers`, `aMUs`, `dictRecentlyTried`), and all `IManUpdater` implementers.

---

## 1. Executive Summary & Root Cause Analysis

The simulation execution in Ostranauts is driven by two tightly coupled layers:
1. **The Sub-stepping Simulation Driver (`CrewSim.Update` -> `AdvanceSim` -> `UpdateICOs`)**: Advances real/virtual time in micro-steps calculated against the earliest ticker across the entire game state.
2. **The Condition Owner & Component Dispatcher (`CondOwner.UpdateManual`, `CondOwner.Cleanup`, and `IManUpdater`)**: Evaluates expired tickers, executes conditioned loot/effects, and iterates through manually updated components.

### Critical Performance Bottlenecks Identified:
1. **Flawed Sub-stepping Logic with O(1)-assumption on Unsorted List**:
   - `CrewSim.Update()` calculates the sub-step size `num3` by checking `CrewSim.aTickers.FirstOrDefault().fNextTickerSecs`.
   - **Bug / Flaw**: `CrewSim.aTickers` is an Ostranauts `UniqueList<CondOwner>` (essentially a `HashSet<T>` + `List<T>`). **It is never sorted at the sim level.** `FirstOrDefault()` simply returns the *first inserted* `CondOwner` in the collection rather than the one with the earliest expiration!
   - Furthermore, because of the fallback `if (num3 < 0.004) num3 = 0.004;`, at high time-acceleration (e.g., 16x scale or autopilot/docking fast-forward where `num = deltaTime * timeScale` can reach 0.5s to 5.0s+), the loop `for (double num2 = num; num2 > 0.0; num2 -= num3)` iterates **hundreds to thousands of sub-steps per frame**.
   - In **every single sub-step**, `this.UpdateICOs()` is invoked, calling `condOwner.UpdateManual(10)` on every active ticker owner in the entire loaded ship!
2. **Repeated Large Allocations & Copies per Sub-step**:
   - `CrewSim.UpdateICOs()` does `CrewSim.aTickersTemp.AddRange(CrewSim.aTickers)` and loops over `aTickersTemp`. When called up to 50–250 times per frame, this triggers massive array copying overhead and GC pressure.
   - Inside `CondOwner.UpdateManual()`, whenever a ticker triggers a loot/update flag:
     - `this.aMUDels.Clear()`
     - `this.aMUs.Clear()`
     - Iterates `this.aManUpdates`, copying non-nulls into `this.aMUs`, nulls into `this.aMUDels`.
     - Iterates `this.aMUs` to call `manUpdater2.UpdateManual()`.
     - Iterates `this.aMUDels` to do `this.aManUpdates.Remove(...)` (which is an O(N) array shift per deleted element!).
3. **Flawed `Cleanup()` Dict Pruning (`dictRecentlyTried`)**:
   - In `CondOwner.Cleanup()`:
     ```csharp
     foreach (string text in new List<string>(this.dictRecentlyTried.Keys))
     {
         if (StarSystem.fEpoch - this.dictRecentlyTried[text] > 60.0)
         {
             this.dictRecentlyTried.Remove(text);
             break; // <-- BREAKS AFTER DELETING EXACTLY ONE ENTRY!
         }
     }
     ```
     - **Leak & Heap Burn**: Allocates `new List<string>(keys)` every 2 simulation seconds per `CondOwner`. But because of the `break;`, it only ever deletes at most **one** expired key per cleanup pass, while AI interactions generate dozens of keys per minute (`co.strID + interaction.strName`). Over extended play sessions, `dictRecentlyTried` steadily bloats, turning `new List<string>` into an ever-growing memory leak and GC spike!
4. **Redundant Dual-Update (`Update()` + `UpdateManual()`) in `IManUpdater`**:
   - Implementers like `BodyTemp`, `GasPump`, `Destructable`, `Wound`, `Sensor`, `Electrical`, and `Meat` are Unity `MonoBehaviour`s with both an active Unity `Update()` method AND an `UpdateManual()` method called by `CondOwner.UpdateManual()`.
   - In many cases (`BodyTemp.cs`, `GasPump.cs`, `Wound.cs`, `Sensor.cs`), `UpdateManual()` literally delegates to `this.Update();`.
   - Because these scripts are attached to active GameObjects, Unity's native engine player loop calls their `Update()` *every rendering frame*, and then `CondOwner` calls their `UpdateManual()` repeatedly in the simulation sub-steps. This results in double-ticking and wasted CPU cycles.

---

## 2. Deep Dive: Code Analysis

### A. `CrewSim.Update` Sub-stepping Loop (`CrewSim.cs:864-880`)
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
        num3 = 0.004; // Minimum step is 4ms (1/250th of a second)
    }
    StarSystem.fEpoch += num3;
    this.AdvanceSim((float)num3);
}
StarSystem.fEpoch = fEpoch;
```

---

## 3. Concrete BepInEx + Harmony Optimization Patches

### Patch 1: Smart Sub-Stepping in `CrewSim.Update`
```csharp
[HarmonyPatch(typeof(CrewSim), "Update")]
public static class Patch_CrewSim_Update
{
    public static void OptimizeSubstepping(CrewSim instance, double num)
    {
        if (num <= 0.0) return;

        const int MAX_SUBSTEPS = 16;
        const double MIN_STEP = 0.016; // 16ms minimum step (~60Hz tick cap)

        double remaining = num;
        int stepCount = 0;

        while (remaining > 0.0 && stepCount < MAX_SUBSTEPS)
        {
            stepCount++;
            double step = remaining;

            double minTicker = double.MaxValue;
            var tickers = CrewSim.aTickers;
            int count = tickers.Count;
            for (int i = 0; i < count; i++)
            {
                var co = tickers[i];
                if (co != null && co.aTickers != null && co.aTickers.Count > 0)
                {
                    double nextSec = co.aTickers[0].fTimeLeft * 3600.0;
                    if (nextSec < minTicker)
                    {
                        minTicker = nextSec;
                        if (minTicker <= 0.0) break;
                    }
                }
            }

            if (minTicker < step && minTicker > 0.0)
            {
                step = minTicker;
            }

            if (step < MIN_STEP)
            {
                step = MIN_STEP;
            }
            if (step > remaining)
            {
                step = remaining;
            }

            StarSystem.fEpoch += step;
            instance.AdvanceSim((float)step);
            remaining -= step;
        }

        if (remaining > 0.0)
        {
            StarSystem.fEpoch += remaining;
            instance.AdvanceSim((float)remaining);
        }
    }
}
```

### Patch 2: Zero-Allocation `CrewSim.UpdateICOs`
```csharp
[HarmonyPatch(typeof(CrewSim), "UpdateICOs")]
public static class Patch_CrewSim_UpdateICOs
{
    private static readonly List<CondOwner> _removalQueue = new List<CondOwner>(32);

    public static bool Prefix(CrewSim __instance)
    {
        CondOwner.nEndTurnsThisFrame = 0;
        var tickers = CrewSim.aTickers;
        int count = tickers.Count;

        _removalQueue.Clear();

        for (int i = 0; i < count; i++)
        {
            CondOwner co = tickers[i];
            if (co == null || co.ship == null || co.ship.bDestroyed || !co.ship.gameObject.activeInHierarchy)
            {
                _removalQueue.Add(co);
            }
            else
            {
                co.UpdateManual(10);
            }
        }

        for (int i = 0; i < _removalQueue.Count; i++)
        {
            CrewSim.RemoveTicker(_removalQueue[i]);
        }
        _removalQueue.Clear();

        if (CrewSim.fTotalGameSec - __instance.fUIUpdateLast > __instance.fUIUpdateHeartbeat)
        {
            __instance.fUIUpdateLast = CrewSim.fTotalGameSec;
        }

        return false;
    }
}
```

### Patch 3: Fix `CondOwner.Cleanup()` Memory Leak & Zero-Allocation Pruning
```csharp
[HarmonyPatch(typeof(CondOwner), "Cleanup")]
public static class Patch_CondOwner_Cleanup
{
    private static readonly List<string> _staleKeys = new List<string>(64);

    public static bool Prefix(CondOwner __instance)
    {
        var dict = __instance.dictRecentlyTried;
        if (dict != null && dict.Count > 0)
        {
            double now = StarSystem.fEpoch;
            _staleKeys.Clear();

            foreach (var kvp in dict)
            {
                if (now - kvp.Value > 60.0)
                {
                    _staleKeys.Add(kvp.Key);
                }
            }

            for (int i = 0; i < _staleKeys.Count; i++)
            {
                dict.Remove(_staleKeys[i]);
            }
            _staleKeys.Clear();
        }

        return true; 
    }
}
```

### Patch 4: In-Place `IManUpdater` Loop in `CondOwner.UpdateManual`
```csharp
var updates = this.aManUpdates;
for (int i = updates.Count - 1; i >= 0; i--)
{
    var mu = updates[i];
    if (mu == null)
    {
        updates.RemoveAt(i);
        continue;
    }
    mu.UpdateManual();
    if (this.bDestroyed)
    {
        CrewSim.RemoveTicker(this);
        break;
    }
}
```
