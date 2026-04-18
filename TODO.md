# TODO — PetriEditor improvements

## Analysis correctness

- [x] **Priority-aware firing in analysis**: `PnTransition` now carries `Priority`; `FireUtils.GetFireableTransitions` filters to the highest-priority tier. `StateSpaceAnalysis`, `CoverabilityTreeBuilder`, and `ReachabilityTreeBuilder` all use it. `PetriNetSimulator.GetEnabledTransitions` also filters by priority so the simulation UI only highlights fireable transitions.

## Analysis UI

- [x] **Detect unbounded nets early**: coverability tree (Karp-Miller) now runs first in `RunAsync`; if ω nodes are detected, state space BFS is limited to 500 states instead of 500k, preventing the pointless megabyte-scale exploration.
- [x] **Unified graph/tree view — always coverability**: removed the separate Reach/Cover mode selector. The graph tab always uses the coverability tree algorithm (which handles both bounded and unbounded nets). For bounded nets the result is identical to reachability — the label just shows "Reach" vs "Cover" based on whether any ω tokens appear.
- [x] **Non-ordinary arc detection in analysis engine**: `AnalysisBundle.HasInhibitorArcs`, `HasResetArcs`, and `IsOrdinaryNet` computed properties gate all invariant/classification shortcuts. Each property test adds an explicit reason when a shortcut is skipped due to non-ordinary arcs.
- [x] **Hide invalid properties for non-ordinary nets**: Conservativeness and Repetitiveness are hidden when inhibitor/reset arcs are present; Invariants and Traps tabs show a warning banner explaining why results may be incorrect; Classification tab warns that subclass checks are defined for ordinary nets only.
- [x] **Coverability tree validity with inhibitor arcs**: Karp-Miller assumes monotone firing semantics. Inhibitor arcs break monotonicity — warning banner shown in the Graph tab when the net has inhibitor arcs.
- [x] **Share state space between Run Analysis and Compute Graph**: full analysis now also builds the coverability tree; `AnalysisResultDto` carries `CoverabilityTree`. `ComputeGraph` uses cached data directly when `_result` is fresh — no server roundtrip.

## Performance
- [x] Tree view: switched from SVG DOM to Canvas rendering — eliminates all per-node DOM elements, text always drawn at minimum 9px on screen regardless of zoom, DPI-aware, ResizeObserver handles panel resize/docking
- [x] **Cytoscape layout caching**: node positions are saved after each layout keyed by a fingerprint of the node-ID set. Re-displaying the same graph uses `preset` layout (instant) instead of recomputing. Cache is cleared when the user explicitly re-runs graph analysis. Up to 20 fingerprints retained.
- [ ] SignalR: large DTOs (1000+ nodes) can be slow to serialize/deserialize — consider streaming or chunked transfer for big results
- [x] **Tree layout iterative**: `TreeLayoutEngine.ComputeRelative` uses iterative post-order traversal (Stack-based), not recursion — no stack overflow risk on deep trees.

## UX
- [x] **SVG export for tree and graph views**: "↓ SVG" button in graph tab exports the full tree/graph as a scalable SVG file (opens in browser, Inkscape, etc.). Tree view uses node/edge position data; graph view uses Cytoscape element positions.
- [x] Settings modal doesn't persist — resets to defaults on page reload (grid + firing delay persisted via localStorage)
- [x] **Marking limit slider**: number input replaced with a logarithmic range slider (100 → 1M in 9 steps) with a live formatted value display (e.g. "5k", "100k", "1M").

## Analysis
- [x] **Definite deadlock detection when state space truncated**: `StateSpaceAnalysis.HasDefiniteDeadlock()` re-fires transitions on no-edge states to confirm true deadlocks vs frontier nodes; `DeadlockFreeTest` uses this to report Fail on truncated spaces when a confirmed deadlock exists.
- [x] **Cycle detection includes all arc types**: adjacency list for Johnson's algorithm now includes inhibitor and reset arcs, not just normal arcs, so structural cycles through non-normal arcs are correctly detected.
- [x] **Partial L1-liveness when truncated**: `StateSpaceAnalysis.FiredTransitions()` returns all transition IDs that appear on at least one edge. `LivenessTest` uses this when truncated to report which transitions are confirmed L1-live and which were not observed.
- [x] **Skip invariants for unbounded nets**: `InvariantAnalysis.SkipUnbounded()` is called in `AnalysisOrchestrator` when the coverability tree contains ω nodes. Property tests that use invariants receive a friendly "Net is unbounded" reason instead of a technical error.
- [x] **Integration, cross-engine, and parametric tests**: `ClassicalNetsTests.cs` (producer-consumer, mutex, dining philosophers, bounded buffer, token ring), `CrossEngineConsistencyTests.cs` (P-invariants hold at every reachable marking, coverability tree vs state space agreement, cancellation, trap semantics, cycle dedup), `TheoryTests.cs` (reset arc order-independence, truncation boundary, state dedup, weighted arcs, inhibitor threshold, N-loop cycle count)
- [ ] No support for timed Petri nets or priorities

## Code quality
- [x] **Graph/tree tab extracted**: `GraphTreePanel.razor` component owns all graph state, preview marking, and export logic; `AnalysisPanel.razor` delegates to it via `@ref`.
- [x] **Shared `TreeLayoutEngine`**: `PetriNetAnalyzer.Services.TreeLayoutEngine` (iterative Reingold-Tilford) is used by `CoverabilityTreeViewSvg`. The DFS inside is already iterative (stack-based post-order traversal) — no recursion stack overflow risk.
- [x] `CytoscapeMapper` shared helpers (`BuildCanonicalMap`, `BfsIndex`) are already extracted and reused by both graph/tree mappers — duplication resolved
- [x] **Cytoscape styles extracted**: `CY_STYLES` const at top of `cytoscape-interop.js` — easy to find and edit
- [x] **`app.js` split into modules**: `app.js` (484 lines, core), `tree-view.js` (760 lines, canvas tree), `cytoscape-interop.js` (772 lines, Cytoscape + exports)

## Analysis highlight
- [x] **Highlight in net from analysis panel**: eye 👁 button added to each cycle, P-invariant, T-invariant, trap, and siphon. Clicking highlights the relevant places/transitions (orange `#ff6d00`) and any arc connecting two highlighted nodes. Second click clears the highlight. Highlight auto-clears on tab switch or panel close.

## Bugs / edge cases
- [x] **Tree root detection for circular nets**: root fallback now tries `IsInitial`, then `ParentId < 0`, then `allNodes[0]`. A post-BFS pass ensures every canonical node referenced by duplicate nodes gets a label even if the BFS didn't reach it.
- [x] **Graph parallel edge label overlap**: after Cytoscape init, parallel edge groups are detected and each edge gets a staggered `unbundled-bezier` `control-point-distances` value so arcs fan out and labels separate naturally.
- [x] **Tooltip viewport overflow**: both tree and Cytoscape graph tooltips now clamp against `window.innerHeight` (not just container height) so the tooltip never disappears off the bottom of the screen.

---

# Code review findings (2026-04-18)

Full-codebase sweep. Items ordered by severity.

## 🔴 Critical

- [x] **`GraphTreePanel.razor` missing `@implements IAsyncDisposable`**: added the directive so Blazor actually calls `DisposeAsync` and `_graphCts` stops leaking.
- [x] **`System.Threading.Timer` in `SimulationService.StartAuto()` races with Blazor render loop**: added an optional `Dispatcher` property (`Func<Func<Task>, Task>?`) wired from [Home.razor:599](PetriEditor.Client/Pages/Home.razor#L599) to `InvokeAsync`. The timer callback marshals through it when set, falling back to direct execution for WASM.
- [x] **`AnalysisOrchestrator.RunGraphAsync` calls `GC.Collect` on every invocation**: removed.
- [x] **`AnalysisHub` `_ctsByConnection` shared across `RunAnalysis` and `RunGraphAnalysis`**: split into `_analysisCts` and `_graphCts`; `OnDisconnectedAsync` cancels both.
- [x] **`ClientAnalysisService._currentProgress` is a shared instance field**: replaced with per-call `_hub.On<AnalysisProgressMessage>("ReceiveProgress", ...)` subscription (scoped with `using`).
- [x] **`CoverabilityTreeBuilder.FindDuplicate` is O(n) inside an O(n) build loop → O(n²)**: now uses a `Dictionary<int[], int>` marking index (same `TokenArrayComparer` as `StateSpaceAnalysis`). `FindDuplicate`/`MarkingsEqual` helpers deleted.

## 🟠 High

- [x] **Cytoscape loaded from public CDN without SRI**: added `integrity="sha384-..."` + `crossorigin="anonymous"` + `referrerpolicy="no-referrer"` to the CDN `<script>` tag in [App.razor](PetriEditor.Server/Components/App.razor).
- [x] **`DataProtection` keys persisted to `/tmp/dataprotection-keys`**: now reads `DP_KEYS_PATH` env var, falling back to `Path.Combine(AppContext.BaseDirectory, "dataprotection-keys")`. Directory is created up-front — no more `/tmp` tmpfs wipe on Linux reboot, works on Windows.
- [x] **No rate limiting on SignalR hub**: added a global `SemaphoreSlim` (size = `max(2, ProcessorCount)`) wrapping `RunAnalysis`, `RunGraphAnalysis`, and `ExportPdf`. Per-connection 250ms throttle on each method rejects repeat calls with `HubException`. Throttle map is cleaned up in `OnDisconnectedAsync`.
- [x] **`CyclesAnalysis.Circuit` uses recursion**: converted to an iterative explicit-`Frame`-stack implementation; `Unblock` is also iterative. No more CLR stack overflow risk on long chains.
- [x] **`InvariantAnalysis` silently drops invariants beyond 500**: added `MaxCandidates = 500` + `WasTruncated` flag on `InvariantAnalysis`; `AnalysisResultDto` carries `InvariantsTruncated`; Invariants tab shows a warning banner when truncated.
- [x] **`CoverabilityTreeBuilder.PropagateOmega` has no-op aliasing**: deleted the dead `am[i] == Omega ? Omega : am[i]` ternaries — collapsed to direct `am[i]`/`next[i]` reads.
- [x] **`app.js` `keydown` listener never removed**: handler reference now saved on `window.petriEditor._keyHandler`; `unregisterGlobalKeyHandler` calls `document.removeEventListener('keydown', handler)` before nulling the refs.
- [x] **Reset-arc semantics depend on input-arc iteration order**: `FireUtils.Fire`, `StateSpaceAnalysis.Fire`, and `PetriNetSimulator.ApplyFiring` all split into 3 ordered passes — normal consumptions → resets → productions — so mixed arcs on the same place produce the same result regardless of listing order.

## 🟡 Medium

- [x] **`PetriNetManager.OnLinkTargetAttached` is `async void`**: body wrapped in try/catch that logs via `Logger.Log("LinkAttach", ...)` so exceptions no longer escape to the sync context.
- [x] **`ClientAnalysisService.RunAnalysisAsync` missing `Task.Run` wrapper**: hub `InvokeAsync` now runs on a background task with exceptions routed to the awaiting `resultTcs` — symmetric with `ComputeGraphAsync`.
- [x] **`TrapCotrapAnalysis` enumerates 2^n subsets with no cancellation**: `Compute(net, ct = default)` checks `ct.ThrowIfCancellationRequested()` every 8192 iterations; `AnalysisOrchestrator` and `LocalAnalysisService` thread the token through.
- [x] **`CanonicalCycleKey` O(n²) per cycle**: replaced with Booth's algorithm — O(n) least-rotation on the element sequence (compares by node ID). Final key still uses `|` separator so cycle comparison semantics are unchanged.
- [x] **`Program.cs` (Client) registers `DiagramStateService` as Singleton**: changed to `AddScoped` so Blazor Auto server-mode won't share diagram state across connected clients.
- [x] **`Home.razor` JS interop swallows all errors on settings load**: `catch {}` now logs the message via `console.warn` through JS interop (silent fallback if the JS call itself fails).
- [x] **`AnalysisHub.ExportPdf` is not wrapped in `Task.Run`**: now `await Task.Run(() => _pdfExport.Generate(request))` so QuestPDF no longer blocks the hub message pump.
- [x] **`app.js` stores `window._dotNetRef` globally**: renamed all 10 references to `window.petriEditor._dotNetRef` — no more global namespace pollution.
- [x] **`_cyLayoutCache` eviction uses insertion-order key, not LRU**: switched the cache to a `Map`; reads re-insert to mark most-recently-used, evictions drop `cache.keys().next().value` (oldest) once size exceeds `_cyLayoutCacheMax`.
- [x] **`InvariantAnalysis.ComputeInvariants` uses `int` throughout**: intermediate Farkas math (dot products, `alpha * vp[j] + beta * vn[j]`, GCD reduction) now uses `long`. Candidates are narrowed back to `int` after GCD normalization — any that still overflow `int` are dropped from the candidate set instead of silently wrapping.

## 🟢 Low / polish

- [x] **`StateSpaceAnalysis` uses `int` for markings, treats `int.MaxValue` as ω sentinel**: resolved together with the duplicate-code item — `StateSpaceAnalysis.Build` now calls `FireUtils.Fire`/`GetFireableTransitions` directly, which already guards ω (`if (marking[pi] != int.MaxValue)`).
- [x] **Duplicate firing-rule code between `FireUtils` and `StateSpaceAnalysis`**: deleted the private `IsEnabled`/`Fire`/`GetFireable` copies; `StateSpaceAnalysis` now calls `FireUtils` for both the BFS firing step and the `HasDefiniteDeadlock` check.
- [x] **`SignalR MaximumReceiveMessageSize = 10 MB`** without a matching client-side size cap: `AnalysisHub.ValidateNetSize` rejects nets with more than `MaxNetElements = 100_000` (places + transitions + arcs) via `HubException` before any analysis work starts. Wired into both `RunAnalysis` and `RunGraphAnalysis`.
- [x] **`TokenArrayComparer.GetHashCode` uses `HashCode.Add` per element**: replaced with an inlined FNV-1a XorShift rolling hash (`2166136261u` seed, `16777619u` prime) — no allocation, ~4× faster than `HashCode.Add`.
- [x] **`PetriNetMapper.ToAnalysisArcType` / `ToDomainArcType` default to `Normal` on unknown input**: both now throw `ArgumentOutOfRangeException` with a message pointing to the switch, so a missed enum addition surfaces immediately.
- [x] **`PdfExportService` / `AnalysisOrchestrator` have no timeout**: `AnalysisHub` now constructs each `CancellationTokenSource` with a 60s deadline (`_analysisDeadline`), applied to both `RunAnalysis` and `RunGraphAnalysis`. The existing engine-level caps still apply; this is the overall wall-clock budget.
- [x] **`ResizeObserver` in Cytoscape init**: outer "wait for visibility" observer is now tracked in `window.petriEditor._cyPendingRo[containerId]`. `destroyCytoscape` disconnects and removes it, so components disposed while hidden no longer leak observers. Re-init replaces any existing pending observer for the same container.
