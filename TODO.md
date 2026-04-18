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

- [ ] **`GraphTreePanel.razor` missing `@implements IAsyncDisposable`**: the component defines `public async ValueTask DisposeAsync()` at line 567 but the file has no `@implements IAsyncDisposable` directive. Blazor only calls DisposeAsync when the component declares the interface, so the method is dead code — `_graphCts` leaks, background JS interop can fire on destroyed refs, and any in-flight compute graph request won't be cancelled when the user navigates away. **Fix:** add `@implements IAsyncDisposable` at the top of `GraphTreePanel.razor`.
- [ ] **`System.Threading.Timer` in `SimulationService.StartAuto()` races with Blazor render loop**: the timer callback directly mutates diagram nodes and raises UI events off a thread-pool thread. In Blazor WASM today this is fine (single-threaded JS), but once the Blazor Auto migration lands, server-mode circuits will deadlock or corrupt state. **Fix:** marshal the tick onto the component's dispatcher (use `ComponentBase.InvokeAsync`) or replace with `PeriodicTimer` driven from `OnAfterRenderAsync`.
- [ ] **`AnalysisOrchestrator.RunGraphAsync` calls `GC.Collect` on every invocation**: `GC.Collect(2, GCCollectionMode.Optimized, blocking: false, compacting: false)` on [AnalysisOrchestrator.cs:161](PetriEditor.Server/Analysis/AnalysisOrchestrator.cs#L161) forces a full Gen 2 collection for *all* concurrent users every time anyone computes a graph. **Fix:** remove the call, or gate it behind the same `RAILWAY_ENVIRONMENT`/`LOW_MEMORY_GC` env var check used in `Program.cs`.
- [ ] **`AnalysisHub` `_ctsByConnection` shared across `RunAnalysis` and `RunGraphAnalysis`**: both methods store into the same dictionary slot keyed by connection id, so invoking `RunGraphAnalysis` while a full analysis is running silently cancels the full analysis (and vice versa). The client has no feedback that this happened. **Fix:** use two separate dictionaries, or key entries by operation name.
- [ ] **`ClientAnalysisService._currentProgress` is a shared instance field**: two concurrent calls to `RunAnalysisAsync` overwrite the shared `IProgress<>` reference, so progress events for the second call fire on the first call's handler. **Fix:** make progress a local captured into the hub subscription, or use a per-call keyed dictionary.
- [ ] **`CoverabilityTreeBuilder.FindDuplicate` is O(n) inside an O(n) build loop → O(n²)**: at `MaxNodes = 100_000` that's up to 10 billion marking comparisons, each an `int[]` sequence-equals over all places. Even for 10k nodes the build stalls for seconds. **Fix:** keep a `Dictionary<int[], int>` (same `TokenArrayComparer` used in `StateSpaceAnalysis`) and look up in O(1).

## 🟠 High

- [ ] **Cytoscape loaded from public CDN without SRI**: [App.razor:20](PetriEditor.Server/Components/App.razor#L20) uses `<script src="https://cdnjs.cloudflare.com/.../cytoscape.min.js">` with no `integrity`/`crossorigin` hash. A CDN compromise or MITM on unencrypted sessions can inject arbitrary JS into every user's editor. **Fix:** either add `integrity="sha384-..."` + `crossorigin="anonymous"`, or vendor the file into `wwwroot/lib/`.
- [ ] **`DataProtection` keys persisted to `/tmp/dataprotection-keys`**: [Program.cs:33](PetriEditor.Server/Program.cs#L33) — `/tmp` is volatile on most Linux distros (tmpfs, cleared on reboot) and nonexistent on Windows. On reboot every issued antiforgery token invalidates, logging users out and breaking form submissions for active sessions. **Fix:** persist to `/var/lib/dataprotection-keys` (or `Path.Combine(AppContext.BaseDirectory, "keys")`) with appropriate permissions, and optionally `.ProtectKeysWith...` for production.
- [ ] **No rate limiting on SignalR hub**: `AnalysisHub.RunAnalysis` runs a potentially multi-second CPU-heavy workload. A single malicious client can spam the hub to exhaust server CPU. **Fix:** add `services.AddRateLimiter(...)` with a per-connection policy, or wrap hub methods in a per-connection `SemaphoreSlim`.
- [ ] **`CyclesAnalysis.Circuit` uses recursion**: Johnson's algorithm at [CyclesTrapAnalysis.cs:35](PetriEditor.Analysis/Engines/CyclesTrapAnalysis.cs#L35) recurses once per node on the cycle path; on nets with long chains (>~2000 nodes combined places+transitions) this stack-overflows the analysis thread. **Fix:** convert to an iterative explicit-stack implementation, mirroring what `StateSpaceAnalysis.FindSCCs` already does.
- [ ] **`InvariantAnalysis` silently drops invariants beyond 500**: [InvariantAnalysis.cs:108](PetriEditor.Analysis/Engines/InvariantAnalysis.cs#L108) — `if (candidates.Count > 500) candidates = candidates.Take(500).ToList();`. The take is unordered (depends on positive/negative pairing order), so which 500 survive is effectively arbitrary. Result: correctness-critical invariants can silently disappear. **Fix:** surface a `WasTruncated` flag on `InvariantAnalysis` and display a warning banner in the Invariants tab (same pattern as state-space truncation).
- [ ] **`CoverabilityTreeBuilder.PropagateOmega` has no-op aliasing**: [CoverabilityTreeBuilder.cs:156-157](PetriEditor.Analysis/Algorithms/CoverabilityTreeBuilder.cs#L156-L157) — `int ai = am[i] == Omega ? Omega : am[i];` and identical for `ni`. The ternary is a no-op; both collapse to `am[i]`/`next[i]`. Suggests an incomplete refactor. **Fix:** either delete the dead branch or implement the intended special-casing (currently ω vs int comparison works by accident because `int.MaxValue` > any finite value).
- [ ] **`app.js` `keydown` listener never removed**: `registerGlobalKeyHandler` attaches a `document.addEventListener('keydown', ...)` but `unregisterGlobalKeyHandler` only nulls `window._dotNetRef`. The handler stays wired, and on Blazor Auto server-mode reconnects it accumulates — every navigation leaks another listener. **Fix:** save the handler reference and call `document.removeEventListener('keydown', handler)` on unregister.
- [ ] **Reset-arc semantics depend on input-arc iteration order**: `FireUtils.Fire` processes input arcs in listing order, applying `next[pi] -= arc.Weight` for Normal and `next[pi] = 0` for Reset. If a transition has both a Normal and a Reset arc on the same place (or a Reset followed by a Normal), the result depends on order. The domain model doesn't prevent such combinations. **Fix:** either reject mixed arcs at the model level, or process all Reset arcs *after* all Normal subtractions in a single pass.

## 🟡 Medium

- [ ] **`PetriNetManager.OnLinkTargetAttached` is `async void`**: exceptions escape to the synchronization context and crash the circuit. **Fix:** convert to `async Task` and invoke via `_ = InvokeAsync(...)` or wrap the body in try/catch that logs to `ILogger`.
- [ ] **`ClientAnalysisService.RunAnalysisAsync` missing `Task.Run` wrapper**: unlike `ComputeGraphAsync`, `RunAnalysisAsync` runs the hub invocation on the UI thread. For big payloads (>1MB serialization) this blocks the renderer. **Fix:** wrap the hub call in `Task.Run` for symmetry with `ComputeGraphAsync`.
- [ ] **`TrapCotrapAnalysis` enumerates 2^n subsets with no cancellation**: [CyclesTrapAnalysis.cs:163](PetriEditor.Analysis/Engines/CyclesTrapAnalysis.cs#L163) loops `1 << n` masks with no `ct.IsCancellationRequested` check. At `MaxPlaces=20` that's a million iterations; cancellation won't fire until the loop finishes. **Fix:** check cancellation every few thousand iterations.
- [ ] **`CanonicalCycleKey` O(n²) per cycle**: [CyclesTrapAnalysis.cs:77](PetriEditor.Analysis/Engines/CyclesTrapAnalysis.cs#L77) rotates and allocates a fresh joined string for every shift. For long cycles and 200-cycle safety cap, this is the hot path. **Fix:** use Booth's algorithm (O(n)) or precompute node-hash arrays.
- [ ] **`Program.cs` (Client) registers `DiagramStateService` as Singleton**: fine in WASM (one browser tab = one WASM app = one singleton), but once the app is hosted under Blazor Auto server-mode, Singleton means shared across *all* users — every connected client sees the same diagram state. **Fix:** change to `AddScoped` before the Auto migration lands.
- [ ] **`Home.razor` JS interop swallows all errors on settings load**: empty `catch {}` in settings restore hides storage-quota / JSON-shape errors. **Fix:** at least `console.warn` or log via `ILogger` so dev can diagnose when settings silently reset.
- [ ] **`AnalysisHub.ExportPdf` is not wrapped in `Task.Run`**: `_pdfExport.Generate` is a CPU-heavy synchronous call (QuestPDF), but it runs on the SignalR hub's message-pump thread, blocking other messages on the same connection. **Fix:** `await Task.Run(() => _pdfExport.Generate(request));`.
- [ ] **`app.js` stores `window._dotNetRef` globally**: namespace pollution, collides if another library uses the same name, and survives Blazor circuit restarts. **Fix:** store on `window.petriEditor._dotNetRef`.
- [ ] **`_cyLayoutCache` eviction uses insertion-order key, not LRU**: [cytoscape-interop.js:283](PetriEditor.Client/wwwroot/cytoscape-interop.js#L283) — `delete cache[keys[0]]` always drops the oldest-inserted, even if it's the one actively displayed. Users who toggle between two graphs watch the one they're *using* keep getting evicted. **Fix:** touch cache entries on access, or use a small `Map` (preserves insertion order and re-set moves to end).
- [ ] **`InvariantAnalysis.ComputeInvariants` uses `int` throughout**: intermediate Farkas coefficients (`alpha * vp[j] + beta * vn[j]`) can overflow silently on moderately connected nets. **Fix:** use `long` or `BigInteger` for intermediate math; `checked` arithmetic at minimum.

## 🟢 Low / polish

- [ ] **`StateSpaceAnalysis` uses `int` for markings, treats `int.MaxValue` as ω sentinel**: the sentinel leaks into firing math (`next[pi] -= arc.Weight` on a `MaxValue` place silently wraps). `FireUtils.Fire` correctly guards against ω but `StateSpaceAnalysis.Fire` (its private copy at [StateSpaceAnalysis.cs:168](PetriEditor.Analysis/Engines/StateSpaceAnalysis.cs#L168)) does not — it's fine today because state space is only built on bounded markings, but it's a latent bug. **Fix:** add the same `int.MaxValue` guard, or document the contract.
- [ ] **Duplicate firing-rule code between `FireUtils` and `StateSpaceAnalysis`**: the private `IsEnabled`/`Fire`/`GetFireable` in `StateSpaceAnalysis` duplicates `FireUtils`. One copy went stale (missing ω guard — see previous item). **Fix:** delete the duplicates; use `FireUtils` directly.
- [ ] **`SignalR MaximumReceiveMessageSize = 10 MB`** without a matching client-side size cap. A large PNML file can be uploaded but may OOM on the orchestrator before hitting a friendly error. **Fix:** validate DTO size early in `RunAnalysis`, reject > a configured threshold.
- [ ] **`TokenArrayComparer.GetHashCode` uses `HashCode.Add` per element**: fine, but for 500k-state builds the per-hash overhead adds up. **Fix:** XorShift rolling hash is ~4× faster for `int[]`.
- [ ] **`PetriNetMapper.ToAnalysisArcType` / `ToDomainArcType` default to `Normal` on unknown input**: silently swallows future arc types. **Fix:** throw `ArgumentOutOfRangeException` so a missed enum addition surfaces immediately.
- [ ] **`PdfExportService` / `AnalysisOrchestrator` have no timeout**: a pathological net can run analysis indefinitely even though `MaxStates` / `MaxNodes` bound each engine — but the full pipeline has no overall budget. **Fix:** wrap `RunAsync` with `CancellationTokenSource.CreateLinkedTokenSource` + configurable deadline (e.g. 60s).
- [ ] **`ResizeObserver` in Cytoscape init: disconnect on `cy.on('destroy')` only covers the inner observer, not the outer "wait for visibility" one**: [cytoscape-interop.js:149](PetriEditor.Client/wwwroot/cytoscape-interop.js#L149) — if the container stays hidden forever, the outer `ResizeObserver` is never disconnected. **Fix:** also disconnect the outer observer on component teardown.
