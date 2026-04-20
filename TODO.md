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
- [x] **SignalR chunked transfer for large graph DTOs**: `AnalysisHub.RunAnalysis` now strips graph/tree data from `ReceiveResult` and streams it as `ReceiveGraphChunk` messages (200 nodes/chunk) followed by `ReceiveGraphDone`. `RunGraphAnalysis` replaced by `StreamGraph` returning `IAsyncEnumerable<GraphChunkDto>` — client uses `StreamAsync` and reassembles. Individual messages stay ≤~60KB regardless of net size.
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

---

# Project sweep (2026-04-20)

Build: OK, 0 errors, 14 warnings. Tests: 209/209 passing (~3s). Full sweep below, ordered by severity.

## 🔴 Critical

- [x] **`DiagramSettings` + `AspNetDiagramLogger` registered as Singleton in [PetriEditor.Client/Program.cs:21-22](PetriEditor.Client/Program.cs#L21-L22)**: both switched to `AddScoped` so Blazor Auto server-mode doesn't share per-user settings/log-buffer state across connected clients. Docstring on `DiagramSettings` updated. Note: CLAUDE.md still calls it a singleton — architecture note is now stale.

## 🟠 High

- [x] **`SimulationService.Dispose` may race with in-flight timer callback in Blazor Server mode ([PetriEditor.Client/Services/SimulationService.cs:350-352](PetriEditor.Client/Services/SimulationService.cs#L350))**: added `volatile bool _disposed` flag checked at every boundary (timer callback entry, dispatcher lambda, `AutoStepOnce`). `Dispose` now uses `Timer.Dispose(WaitHandle)` + `WaitOne()` to block until any in-flight callback completes. Queued dispatcher callbacks that fire after disposal see `_disposed=true` and return immediately.
- [ ] **[PetriEditor.Client/DiagramModels/PetriLinkModel.cs:45](PetriEditor.Client/DiagramModels/PetriLinkModel.cs#L45) — CS8604**: `base(sourceAnchor, targetAnchor)` passes a nullable `targetAnchor` to a non-nullable parameter. `Z.Blazor.Diagrams.Core.Models.LinkModel` will deref it. The factory path that actually sets Target later works, but a caller passing `null` here now would NRE inside the base ctor. Either mark the base param as accepting null (document the Z.Blazor Diagrams contract) or throw at our ctor if `targetAnchor` is null when the base requires it.
- [x] **[PetriEditor.Client/Components/Widgets/PetriLinkWidget.razor:224](PetriEditor.Client/Components/Widgets/PetriLinkWidget.razor#L224) — CS8602**: mirrored the `Target?.` guard on `Source`. `srcPos` now uses `link.Source?.GetPosition(...)` and the existing `if (srcPos != null)` gate downstream already handles the null case.

## 🟡 Medium

- [ ] **[PetriEditor.Client/Services/PetriNetManager.cs:536](PetriEditor.Client/Services/PetriNetManager.cs#L536) — `async void OnLinkTargetAttached`**: try/catch is present (good), but the handler subscribes to `TargetAttached` on each `PetriLinkModel` and unsubscribes in `OnLinkRemoved`. If a link's Target swaps without the link being removed, the handler stays subscribed and fires again — confirmed OK for now since Target is immutable in Z.Blazor after attach, but fragile. Consider `async Task` + `InvokeAsync` wrapper so exceptions flow through Blazor's rendering pipeline.
- [x] **[PetriEditor.Client/Services/DiagramSettings.cs — CS8618](PetriEditor.Client/Services/DiagramSettings.cs)**: `ArcColor`, `ArcSelectedColor`, `ArcPendingColor` now initialized to `""` at the property declaration. `Apply(_defaults)` still overwrites them from appsettings.json before anything reads them.
- [x] **[PetriEditor.Client/Components/AnalysisPanel.razor:686 — CS8321](PetriEditor.Client/Components/AnalysisPanel.razor#L686)**: deleted the unused `ComputeTInvariantWeight` local function.
- [x] **[PetriEditor.Client/Components/AnalysisPanel.razor `SetupJsAsync` catch](PetriEditor.Client/Components/AnalysisPanel.razor)**: replaced bare `catch { }` with `catch (Exception ex) { Console.Error.WriteLine(...) }` so JS setup failures surface in the browser console.
- [x] **[PetriEditor.Client/Pages/Home.razor nested `catch { }`](PetriEditor.Client/Pages/Home.razor)**: inner fallback catch now logs via `Console.Error.WriteLine` when both the primary load and the `console.warn` JS interop fail.
- [x] **[PetriEditor.Client/Components/CoverabilityTreeViewSvg.razor:303](PetriEditor.Client/Components/CoverabilityTreeViewSvg.razor#L303)**: added a one-line comment documenting why the `treeView.destroy` catch is intentionally silent (teardown path; JS module may already be gone).
- [x] **[PetriEditor.Client/Services/PetriNetManager.cs — CS8604](PetriEditor.Client/Services/PetriNetManager.cs)**: `standaloneLinks` filter now resolves `GetParentNode` once per link and explicitly rejects links where either parent is null, before the `HashSet.Contains` call.
- [x] **[PetriEditor.Client/Components/Widgets/PetriLinkWidget.razor:249 — CS8604](PetriEditor.Client/Components/Widgets/PetriLinkWidget.razor#L249)**: `OnWeightLabelPointerDown` now early-returns when `_points is null`. Avoids passing a null list to `ResolvedWeightSegment`.
- [x] **[PetriEditor.Client/Components/Widgets/PetriLinkWidget.razor:166-167 — CS8618](PetriEditor.Client/Components/Widgets/PetriLinkWidget.razor#L166-L167)**: `_dragStartMouse`/`_dragStartVertex` initialized to `new(0, 0)` at declaration. (Turns out `Point` is a class in Z.Blazor.Diagrams 3.x, not a struct — the warning was legitimate.)

## 🟢 Low / polish

- [ ] **`NU1510` Microsoft.AspNetCore.DataProtection package warning in [PetriEditor.Server.csproj](PetriEditor.Server/PetriEditor.Server.csproj)**: NuGet reports the package as unnecessary because DataProtection is included in the ASP.NET Core shared framework on .NET 10. Remove the explicit `<PackageReference>` — the `.AddDataProtection()` extension resolves from the framework. (Needs explicit permission per CLAUDE.md off-limits.)
- [x] **[PetriEditor.Client/Components/DiagramNodes/TransitionComponent.razor:23 — CS8604](PetriEditor.Client/Components/DiagramNodes/TransitionComponent.razor#L23)**: replaced the inline ternary `@onclick` with a named `OnClick()` method that checks `IsSimulating`, null model, and enabled-state inside the method body. Removes the conditional null callback entirely.
- [x] **[PetriEditor.Client/DiagramModels/PetriLinkModel.cs:45 — CS8604](PetriEditor.Client/DiagramModels/PetriLinkModel.cs#L45)**: added the null-forgiving operator `targetAnchor!` at the base ctor call with a comment explaining that Z.Blazor's non-nullable signature lies — a null target is how dragging/pending links are represented.
- [ ] **`FileLoggerProvider` holds an `AutoFlush=true StreamWriter` for the process lifetime ([PetriEditor.Server/Logging/FileLoggerProvider.cs:18](PetriEditor.Server/Logging/FileLoggerProvider.cs#L18))**: AutoFlush per-line is fine for low-volume logs, but there's no rotation — the file grows unbounded. Add size-based rotation or rely on the hosting platform's log cap. Note this is the file the user had open in IDE (`logs/petri.log`).
- [ ] **`ConcurrentDictionary` use in [PetriEditor.Server/Hubs/AnalysisHub.cs:28,37](PetriEditor.Server/Hubs/AnalysisHub.cs#L28)**: legitimate — SignalR hub methods execute concurrently across connections on the server. Kept for audit trail only; not a bug. (The CLAUDE.md "no ConcurrentDictionary" note applies to the WASM client, not the server project.)
- [x] **No cancellation path in [PetriEditor.Server/Hubs/AnalysisHub.cs:184 `ExportPdf`](PetriEditor.Server/Hubs/AnalysisHub.cs#L184)**: `ExportPdf` now constructs its own `CancellationTokenSource(_analysisDeadline)`, threads the token into both `_heavyOpGate.WaitAsync` and `Task.Run`, and converts timeout to `HubException` so the client sees a clean error.

---

## Summary
- Build: clean (0 errors, 14 warnings → 2 warnings; only NU1510 package-prune remain).
- Tests: 209 pass.
- Real bugs fixed: 3 (Singleton DI in Blazor Auto, SimulationService timer race on dispose, PetriLinkWidget.Source null-deref path).
- Nullable warnings fixed: 8.
- Dead code removed: 1 (`ComputeTInvariantWeight`).
- Silent exception swallows patched: 3.
- Server-side: `ExportPdf` now has a 60s deadline matching the other hub methods.
- Remaining: `OnLinkTargetAttached` async-void fragility (deferred — requires broader event-subscription refactor), NU1510 package prune (requires csproj edit permission).
