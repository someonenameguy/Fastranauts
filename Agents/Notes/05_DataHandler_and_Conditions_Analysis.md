# Subagent 5 Complete Report: DataHandler, Conditions & Reflection Architecture Analysis

## 1. Executive Summary
An in-depth analysis of decompiled `DataHandler.cs`, `CondOwner.cs`, `Condition.cs`, and `CondTrigger.cs` was performed. We identified significant optimization opportunities regarding dictionary double-lookups, hot-path string allocation in threshold checks, condition wrapper instantiations, and uncached reflection operations.

---

## 2. Key Performance Bottlenecks Identified

### A. Redundant Double Lookups in DataHandler Hot Paths
In `DataHandler.cs`, multiple frequently called lookup methods invoke `.ContainsKey(key)` followed immediately by `dict[key]`:
- **`GetLoot(string strName)`** (Lines 2051-2066): Called continuously across AI updates, shift changes, and condition looting.
- **`GetItemDef(string strName)`** (Lines 2122-2130): Invoked repeatedly when inspecting, equipping, moving, or loading items.
- **`GetInteraction(string strName, ...)` & `TryGetInteraction`** (Lines 2902-2930): Invoked in interactions polling and AI decision matrices.
- **`GetCondFriendlyName(string strCondName)`** (Lines 2883-2895): Invoked in UI tooltips, logs, and dialog rows.
- **`GetLight`, `GetPledge`**: Suffer from the same double lookup pattern.
*Impact*: Every call calculates string hashes and scans internal buckets twice instead of once.

### B. Transient String Allocations in Threshold Lookups
In `CondOwner.cs`:
- **`GetCondAmount(string strName, bool isThreshold)`** (Lines 4679-4705)
- **`HasCond(string strName, bool isThreshold)`** (Lines 4650-4664)
- **`AddCondAmount(string strName, ...)`** (Lines 4214-4235)
Whenever `isThreshold` is true (i.e., starts with `"Thresh"`), the code calls:
```csharp
string text = strName.Substring(6);
this.mapCondRules.TryGetValue(text, out condRule);
```
`GetCondAmount` is called thousands of times per second (in `BodyTemp.cs`, `Ship.cs`, `Wound.cs`, AI decision routines, UI bars). Every threshold query allocates a new string on the heap, driving constant Gen0 garbage collections.

### C. Condition Object Churn in DataHandler.GetCond
In `DataHandler.cs`:
```csharp
public static Condition GetCond(string strName) {
    if (strName == null) return null;
    JsonCond jsonCond;
    if (DataHandler.dictConds.TryGetValue(strName, out jsonCond) && jsonCond != null) {
        return new Condition(DataHandler.dictConds[strName]);
    }
    return null;
}
```
`Condition` constructor (Lines 8-99 of `Condition.cs`) instantiates two new `List` instances (`aNext = new List<CondTrigger>()` and `aPer = new List<string>()`), copies face sprites, and parses triggers.
Many external callers (e.g. `GUIChargenCareer`, `GUISocialsRow`, `PDAVisualisers`, `GasContainer`) call `DataHandler.GetCond(name)` solely to inspect read-only properties (`strNameFriendly`, `fClampMax`, `fDuration`). Creating new `Condition` wrapper instances for metadata queries produces substantial heap churn.

### D. CondTrigger Deep Cloning and String Formatting
- `DataHandler.GetCondTrigger(strName, bool bClone = true)` clones the trigger by default.
- In `CondTrigger.Triggered` (Lines 608-826 of `CondTrigger.cs`), string concatenations for failure reasons (`"Chance: " + num + " / " + fChance`, `"Lacking: (" + ...`) run during failure checks, generating temporary strings.

### E. Inefficient Linear Collection Scans
- **`CondOwner.aDestructableConds`** (`List<string>`): Checked via `.Contains()` on every `AddCondAmount` call (Line 4621).
- **`CondOwner.aCondsTimed`** (`List<Condition>`): Conditions expiring or reaching count <= 0 are removed via `this.aCondsTimed.Remove(cond)` (Line 4395), which performs an O(N) scan and array element shift.

### F. Uncached Reflection Operations in DataHandler
- **`DataHandler.JsonToData<TJson>`** (Line 1051): Calls `tjson.GetType().GetProperty("strName")` inside the item loop for every JSON object deserialized during startup.
- **`DataHandler.ApplyOverride`** (Line 978): Performs `type.GetProperty(array[0])` for every override line without caching.
- **`DataHandler.GetCommand`** (Line 2604): Calls `Type.GetType("Ostranauts.Ships.Commands." + name)` without a type cache.

---

## 3. Concrete Optimization Patches (Harmony Ready)

### Patch 1: Replace ContainsKey + Indexer with TryGetValue in DataHandler
```csharp
[HarmonyPatch(typeof(DataHandler), nameof(DataHandler.GetLoot))]
public static class Patch_DataHandler_GetLoot
{
    public static bool Prefix(string strName, ref Loot __result)
    {
        if (strName == null) { __result = new Loot(); return false; }
        if (DataHandler.dictLoot.TryGetValue(strName, out var loot) && loot != null)
        {
            __result = loot;
            return false;
        }
        if (strName.Length > 0)
        {
            Debug.Log("#Info# Unable to load Loot: " + strName);
        }
        __result = new Loot();
        return false;
    }
}

[HarmonyPatch(typeof(DataHandler), nameof(DataHandler.GetItemDef))]
public static class Patch_DataHandler_GetItemDef
{
    public static bool Prefix(string strName, ref JsonItemDef __result)
    {
        if (strName == null) { __result = null; return false; }
        if (DataHandler.dictItemDefs.TryGetValue(strName, out var itemDef))
        {
            __result = itemDef;
            return false;
        }
        Debug.Log("Unable to load Item Def: " + strName);
        __result = null;
        return false;
    }
}
```

### Patch 2: Zero-Allocation Threshold Stripping Cache in CondOwner
```csharp
public static class ThresholdStringPool
{
    private static readonly Dictionary<string, string> _strippedCache = new Dictionary<string, string>(512, StringComparer.Ordinal);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string GetStripped(string threshName)
    {
        if (!_strippedCache.TryGetValue(threshName, out var stripped))
        {
            stripped = threshName.Substring(6);
            _strippedCache[threshName] = stripped;
        }
        return stripped;
    }
}

[HarmonyPatch(typeof(CondOwner), nameof(CondOwner.GetCondAmount), new[] { typeof(string), typeof(bool) })]
public static class Patch_CondOwner_GetCondAmount
{
    public static bool Prefix(CondOwner __instance, string strName, bool isThreshold, ref double __result)
    {
        if (strName == null || __instance.mapConds == null)
        {
            __result = 0.0;
            return false;
        }
        if (isThreshold)
        {
            string stripped = ThresholdStringPool.GetStripped(strName);
            if (__instance.mapCondRules != null && __instance.mapCondRules.TryGetValue(stripped, out var rule) && rule != null)
            {
                __result = rule.Modifier;
                return false;
            }
            __result = 0.0;
            return false;
        }
        if (__instance.mapConds.TryGetValue(strName, out var cond) && cond != null)
        {
            __result = cond.fCount;
            return false;
        }
        __result = 0.0;
        return false;
    }
}
```

### Patch 3: Direct Non-Allocating Condition Metadata Access (`GetCondDef`)
Add static helper for metadata lookups so callers do not construct `new Condition(...)`:
```csharp
public static JsonCond GetCondDef(string strName)
{
    if (strName == null) return null;
    DataHandler.dictConds.TryGetValue(strName, out var jsonCond);
    return jsonCond;
}
```
Redirect tooltip and status bar queries (`GetCond(x).strNameFriendly`) to `GetCondDef(x).strNameFriendly`.

### Patch 4: Reflection PropertyInfo and Type Caches in DataHandler
Cache `PropertyInfo` by `Type` for `JsonToData<TJson>`:
```csharp
private static readonly Dictionary<Type, PropertyInfo> _namePropCache = new Dictionary<Type, PropertyInfo>();

// In JsonToData:
if (!_namePropCache.TryGetValue(typeof(TJson), out var prop))
{
    prop = typeof(TJson).GetProperty("strName");
    _namePropCache[typeof(TJson)] = prop;
}
```

### Patch 5: Set Indexing for CondOwner Lists
- Maintain an internal `HashSet<string> _hashDestructableConds` alongside `aDestructableConds` to make `aDestructableConds.Contains(...)` O(1).
- Use `HashSet<Condition>` or swap-with-back removal for `aCondsTimed` to avoid O(N) array copying on condition expiration.
