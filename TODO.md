# TODO — PetriEditor improvements

## Analysis correctness

- [ ] **Priority-aware firing in analysis**: `TransitionDto` already has a `Priority` field (stored, editable in the UI) but the analysis engine (`StateSpaceAnalysis`, `ReachabilityTreeBuilder`, `CoverabilityTreeBuilder`) ignores it — all enabled transitions are treated as equally fireable. If priorities are intended to restrict which transition fires (e.g. highest-priority wins when multiple are enabled), the state space BFS needs to filter enabled transitions by priority tier before exploring. Decide the semantics (strict priority or just ordering hint) first.

## Analysis UI

- [x] **Detect unbounded nets early**: coverability tree (Karp-Miller) now runs first in `RunAsync`; if ω nodes are detected, state space BFS is limited to 500 states instead of 500k, preventing the pointless megabyte-scale exploration.
- [x] **Unified graph/tree view — always coverability**: removed the separate Reach/Cover mode selector. The graph tab always uses the coverability tree algorithm (which handles both bounded and unbounded nets). For bounded nets the result is identical to reachability — the label just shows "Reach" vs "Cover" based on whether any ω tokens appear.
- [ ] **Non-ordinary arc detection in analysis engine**: expose a `NetCapabilities` flag on `AnalysisBundle` so property tests can skip invariant/classification shortcuts when inhibitor or reset arcs are present (currently the reasoning is silently skipped at the test level but there is no central gating).
- [x] **Hide invalid properties for non-ordinary nets**: Conservativeness and Repetitiveness are hidden when inhibitor/reset arcs are present; Invariants and Traps tabs show a warning banner explaining why results may be incorrect; Classification tab warns that subclass checks are defined for ordinary nets only.
- [ ] **Coverability tree validity with inhibitor arcs**: Karp-Miller assumes monotone firing semantics. Inhibitor arcs break monotonicity — show a warning in the Graph tab when the net has inhibitor arcs.
- [x] **Share state space between Run Analysis and Compute Graph**: full analysis now also builds the coverability tree; `AnalysisResultDto` carries `CoverabilityTree`. `ComputeGraph` uses cached data directly when `_result` is fresh — no server roundtrip.

## Performance
- [x] Tree view: switched from SVG DOM to Canvas rendering — eliminates all per-node DOM elements, text always drawn at minimum 9px on screen regardless of zoom, DPI-aware, ResizeObserver handles panel resize/docking
- [ ] Cytoscape graph: large graphs (500+ nodes) are slow on layout computation — consider caching layout positions and only re-running layout when data changes
- [ ] SignalR: large DTOs (1000+ nodes) can be slow to serialize/deserialize — consider streaming or chunked transfer for big results
- [ ] Tree layout `ComputeRelative` uses recursive DFS — could stack overflow on very deep trees (>5000 levels); convert to iterative if needed

## UX
- [ ] Tree view: no click-to-apply-marking like the graph view has (graph sets marking on the net when you click a node)
- [ ] No way to export tree/graph as image (SVG export or PNG screenshot)
- [ ] Settings modal doesn't persist — resets to defaults on page reload (could use localStorage)
- [ ] Marking limit slider would be more intuitive than a number input for quick adjustments
- [ ] No visual diff when re-running analysis — hard to tell what changed between runs

## Analysis
- [ ] When state space is truncated, deadlock detection is disabled entirely — could try to identify "definite deadlocks" (states where no transition has its preconditions met, regardless of truncation)
- [ ] No partial liveness analysis when truncated — could report "at least L1-live" for transitions that fire in the partial space
- [ ] Invariant computation runs independently of state space — could skip it if the net is trivially unbounded
- [ ] No support for timed Petri nets or priorities
- [ ] Cycle detection doesn't handle self-loops as a special case

## Code quality
- [ ] `AnalysisPanel.razor` is very large (~1400 lines) — could extract the graph/tree tab into its own component
- [ ] Duplicate layout logic between `ReachabilityTreeView.razor` and `CoverabilityTreeViewSvg.razor` — could extract shared `TreeLayoutEngine` class
- [ ] `CytoscapeMapper` duplicates contour-merge logic — shared with tree views but slightly different
- [ ] Some Cytoscape styles are hardcoded in JS — could move to a shared config or CSS
- [ ] `app.js` is growing large (~1200 lines) — could split into modules (tree, cytoscape, diagram)

## Bugs / edge cases
- [ ] Tree view: if all nodes are references (circular net), the root detection (`FirstOrDefault(n => n.IsInitial)`) might fail
- [ ] Graph view: parallel edges between same nodes overlap — Cytoscape `bezier` curve-style handles this but labels can still overlap
- [ ] Tree tooltip position can overflow below the viewport if the tree is scrolled to the bottom
