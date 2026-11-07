# Handoff: Runtime Profiler Screenshot Analysis Complete

## Status
- **Phase**: Runtime Profiler Screenshot Analysis & Code Trace (Completed)
- **Active Agents**: None (`Progress\Agent_Profiler_Analysis.txt` removed upon completion).

## Deliverables Written / Updated
1. `Notes\11_Profiler_Screenshot_Analysis.md` — Line-by-line decompiled C# trace for every non-BepInEx bottleneck in the runtime profiler screenshot (`PDAVisualisers::Update`, `CrewSim::Update`, `GasPressureSense::Update`, `Visibility::LateUpdate`, `GUIItemList::Update`, `GasPump::Update`, `FusionIC::Update`).
2. `Notes\Master_Optimization_Synthesis_and_Roadmap.md` — Updated with Section 11 and elevated `PDAVisualisers`, `GasPressureSense`, and `Visibility` into **P0 Critical Priorities**.

## Key Findings from the Profiler Screenshot
- **`pnlViz > PDAVisualisers::Update` (`24.20 ms` | `211,306 B`)**:
  - `NotifyShips()` runs every 0.5s when live overlays are active (`_togglePingUpdates = true`) and calls `ship.VisualizeOverlays(true)`.
  - Passing `force = true` bypasses the dirty check at `Item.cs:169`, forcing `rend.GetPropertyBlock`/`SetPropertyBlock` across thousands of unchanged items, while `Ship.VisualizeOverlays` clones `"Blank"` `CondTrigger` and allocates massive `List<CondOwner>` collections via `GetCOs`.
- **`PlayState > CrewSim::Update` (`3.80 ms` | `22,072 B`)**:
  - Driven by `Physics.RaycastAll` in `MouseHandler()`, `aTickersTemp.AddRange`, and `CondOwner.Cleanup()` dictionary key list allocations.
- **`ItmDoor01OpenOn > GasPressureSense::Update` (`0.21 ms` | `2,219 B` per door)**:
  - Each door sensor allocates `new List<CondOwner>()`, clones `DataHandler.GetCondTrigger(this.strCTClear)` (`bClone = true` default), and executes `Physics.RaycastAll` in `GetCOsAtWorldCoords1`.
- **`ArmLLamp > Visibility::LateUpdate` (`0.06 ms` | `715–933 B` per lamp)**:
  - `ResetLightMesh()` and `AddOccludersFromCrewSimBlocks()` allocate 6 `new List<T>()` instances per frame, and `LateUpdate()` allocates 3 arrays via `.ToArray()` instead of `Mesh.SetVertices(List<Vector3>)`.
- **`ItemList > GUIItemList::Update` (`0.06 ms` | `144 B`)**:
  - Rebuilds `_itemList` and `_pressureList` strings every frame even when hovered objects and pressures haven't changed.

## Next Steps for Implementation Agent
- Proceed with scaffolding the BepInEx C# solution (`Fastranauts`) starting with the **P0** patches (`PDAVisualisers`, `CrewSim`, `GasPressureSense`, `Visibility`, `GUIInventoryWindow`, and `GUIItemList`).
