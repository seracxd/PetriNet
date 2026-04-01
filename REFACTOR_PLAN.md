# Petri Net Editor — Refactoring Plan

**Goal:** Migrate from Blazor WebAssembly (no server) to Blazor Auto (server + WASM hybrid)  
**Stack:** .NET 10, Blazor Auto, SignalR, QuestPDF, Cytoscape.js  
**Phases:** 8 (work through them in order — each phase must build and pass verification before starting the next)

---

## Architecture Overview

```
PetriEditor/
├── PetriEditor.Shared/      # Domain models + service interfaces (no UI, no server deps)
├── PetriEditor.Analysis/    # Pure analysis algorithms (no UI)
├── PetriEditor.Server/      # ASP.NET Core host + SignalR hubs + PDF export
└── PetriEditor.Client/      # Blazor WASM components + client-side services
```

**Client/Server contract:**
- Client sends `PetriNetDto` to server via SignalR
- Server runs analysis and streams `AnalysisProgressMessage` back
- Server returns `AnalysisResultDto` when done
- PDF export runs on server, downloaded to browser
- TikZ and PNML generation runs in browser (WASM) — no server round-trip needed

---

## Phase 1 — Solution Restructure

**Goal:** Create 4 new projects, move source files (namespaces unchanged for now), delete Infrastructure, verify the solution builds.

### New Project Files

**`PetriEditor.Shared/PetriEditor.Shared.csproj`**
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
</Project>
```

**`PetriEditor.Analysis/PetriEditor.Analysis.csproj`**
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\PetriEditor.Shared\PetriEditor.Shared.csproj" />
  </ItemGroup>
</Project>
```

**`PetriEditor.Server/PetriEditor.Server.csproj`**
```xml
<Project Sdk="Microsoft.NET.Sdk.Web">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="QuestPDF" Version="2024.10.4" />
    <ProjectReference Include="..\PetriEditor.Shared\PetriEditor.Shared.csproj" />
    <ProjectReference Include="..\PetriEditor.Analysis\PetriEditor.Analysis.csproj" />
    <ProjectReference Include="..\PetriEditor.Client\PetriEditor.Client.csproj" />
  </ItemGroup>
</Project>
```

**`PetriEditor.Client/PetriEditor.Client.csproj`**
```xml
<Project Sdk="Microsoft.NET.Sdk.BlazorWebAssembly">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.AspNetCore.Components.WebAssembly" Version="10.0.0" />
    <PackageReference Include="Microsoft.AspNetCore.SignalR.Client" Version="9.0.0" />
    <PackageReference Include="Z.Blazor.Diagrams" Version="3.0.4" />
    <PackageReference Include="Z.Blazor.Diagrams.Core" Version="3.0.4" />
    <ProjectReference Include="..\PetriEditor.Shared\PetriEditor.Shared.csproj" />
  </ItemGroup>
</Project>
```

### File Moves (Source → Destination)

| Source (old) | Destination (new) |
|---|---|
| `Core/Models/PetriNetNode.cs` | `PetriEditor.Shared/Models/` |
| `Core/Models/Place.cs` | `PetriEditor.Shared/Models/` |
| `Core/Models/Transition.cs` | `PetriEditor.Shared/Models/` |
| `Core/Models/Arc.cs` | `PetriEditor.Shared/Models/` |
| `Analysis/PetriNetModel.cs` | `PetriEditor.Shared/Analysis/` |
| `Analysis/PropertyTestResult.cs` | `PetriEditor.Shared/Analysis/` |
| `Analysis/AnalysisService.cs` | `PetriEditor.Analysis/` |
| `Analysis/Algorithms/*.cs` | `PetriEditor.Analysis/Algorithms/` |
| `Analysis/Engines/*.cs` | `PetriEditor.Analysis/Engines/` |
| `Analysis/Simulation/PetriNetSimulator.cs` | `PetriEditor.Analysis/Simulation/` |
| `PetriNetAnalizer/DiagramModels/*.cs` | `PetriEditor.Client/DiagramModels/` |
| `PetriNetAnalizer/Services/*.cs` | `PetriEditor.Client/Services/` |
| `PetriNetAnalizer/Services/History/*.cs` | `PetriEditor.Client/Services/History/` |
| `PetriNetAnalizer/Components/**` | `PetriEditor.Client/Components/` |
| `PetriNetAnalizer/Pages/**` | `PetriEditor.Client/Pages/` |
| `PetriNetAnaliter/Layout/**` | `PetriEditor.Client/Layout/` |
| `PetriNetAnalizer/wwwroot/**` | `PetriEditor.Client/wwwroot/` |
| `PetriNetAnalizer/Program.cs` | `PetriEditor.Client/Program.cs` |
| `PetriNetAnalizer/App.razor` | `PetriEditor.Client/App.razor` |
| `PetriNetAnalizer/_Imports.razor` | `PetriEditor.Client/_Imports.razor` |
| `PetriNetAnalizer/appsettings.json` | `PetriEditor.Client/wwwroot/appsettings.json` |

Delete: `Infrastructure/` project, old `Core/` project, old `Analysis/` project, old `PetriNetAnalizer/` project.

### Server Bootstrap `PetriEditor.Server/Program.cs`

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents()
    .AddInteractiveWebAssemblyComponents();
builder.Services.AddSignalR();

var app = builder.Build();
app.UseStaticFiles();
app.UseAntiforgery();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .AddInteractiveWebAssemblyRenderMode()
    .AddAdditionalAssemblies(typeof(PetriEditor.Client._Imports).Assembly);
app.MapHub<AnalysisHub>("/hubs/analysis");
app.Run();
```

### Verify
- `dotnet build` on solution produces zero errors
- `dotnet run --project PetriEditor.Server` starts without crash

---

## Phase 2 — Shared Contracts

**Goal:** Define all DTOs and service interfaces for client-server communication. No Blazor or ASP.NET dependencies allowed in `PetriEditor.Shared`. Consolidate the duplicate `ArcType` enum.

### ArcType Consolidation

Delete `Analysis.PnArcType`. Replace all its usages with `PetriEditor.Shared.Models.ArcType`.

New file `PetriEditor.Shared/Models/ArcType.cs`:
```csharp
namespace PetriEditor.Shared.Models;
public enum ArcType { Normal, Inhibitor, Reset }
```

Update `PetriNetSnapshot` (was `PnArcType`) to use `ArcType`.

### DTOs — `PetriEditor.Shared/Contracts/`

**`PetriNetDto.cs`**
```csharp
namespace PetriEditor.Shared.Contracts;

public sealed record PetriNetDto(
    IReadOnlyList<PlaceDto>      Places,
    IReadOnlyList<TransitionDto> Transitions,
    IReadOnlyList<ArcDto>        Arcs);

public sealed record PlaceDto(
    string Id,
    string Name,
    int    Tokens,
    double X,
    double Y);

public sealed record TransitionDto(
    string Id,
    string Name,
    int    Priority,
    double X,
    double Y);

public sealed record ArcDto(
    string  SourceId,
    string  TargetId,
    int     Weight,
    ArcType ArcType,
    IReadOnlyList<PointDto> Vertices);

public sealed record PointDto(double X, double Y);
```

**`AnalysisProgressMessage.cs`**
```csharp
namespace PetriEditor.Shared.Contracts;

public sealed record AnalysisProgressMessage(
    string  Stage,      // "StateSpace" | "Invariants" | "Classification" | "Cycles" | 
                        // "ReachabilityTree" | "CoverabilityTree" | "PropertyTests" | "Complete" | "Error"
    int     Percent,    // 0–100
    string? ErrorText); // non-null when Stage == "Error"
```

**`AnalysisResultDto.cs`**
```csharp
namespace PetriEditor.Shared.Contracts;

public sealed record AnalysisResultDto(
    int                            StateCount,
    bool                           IsBounded,
    bool                           IsDeadlockFree,
    bool                           IsReversible,
    bool                           IsSafe,
    bool                           IsLive,
    string                         ClassificationSummary,
    IReadOnlyList<PropertyResultDto>  PropertyResults,
    IReadOnlyList<InvariantDto>       PInvariants,
    IReadOnlyList<InvariantDto>       TInvariants,
    ReachabilityGraphDto?             ReachabilityGraph,
    CoverabilityTreeDto?              CoverabilityTree);

public sealed record PropertyResultDto(
    string                        Property,   // NetProperty.ToString()
    string                        Status,     // TestResultStatus.ToString()
    IReadOnlyList<string>         Reasons,
    IReadOnlyList<string>         Errors);

public sealed record InvariantDto(
    IReadOnlyDictionary<string, int> Structure);

public sealed record ReachabilityGraphDto(
    IReadOnlyList<ReachNodeDto> Nodes,
    IReadOnlyList<ReachEdgeDto> Edges);

public sealed record ReachNodeDto(
    int              Id,
    IReadOnlyList<int> Marking,
    bool             IsInitial,
    bool             IsDeadlock,
    bool             IsDuplicate,
    int              ParentId);   // -1 for root

public sealed record ReachEdgeDto(
    int    From,
    int    To,
    string TransitionId,
    string TransitionName);

public sealed record CoverabilityTreeDto(
    IReadOnlyList<CoverNodeDto> Nodes,
    IReadOnlyList<CoverEdgeDto> Edges);

public sealed record CoverNodeDto(
    int                   Id,
    IReadOnlyList<int?>   Marking,   // null = ω (omega)
    bool                  IsInitial,
    bool                  IsDeadlock,
    bool                  IsDuplicate,
    int                   ParentId);

public sealed record CoverEdgeDto(
    int    From,
    int    To,
    string TransitionId,
    string TransitionName);
```

**`ExportRequestDto.cs`**
```csharp
namespace PetriEditor.Shared.Contracts;

public enum ExportFormat { Pdf, TikZ, Pnml }

public sealed record ExportRequestDto(
    PetriNetDto   Net,
    ExportFormat  Format,
    ExportOptions Options);

public sealed record ExportOptions(
    string             DocumentTitle   = "Petri Net",
    bool               IncludeAnalysis = false,
    AnalysisResultDto? AnalysisResult  = null);
```

### Service Interfaces — `PetriEditor.Shared/Contracts/`

**`IAnalysisService.cs`**
```csharp
namespace PetriEditor.Shared.Contracts;

public interface IAnalysisService
{
    Task<AnalysisResultDto> RunAnalysisAsync(
        PetriNetDto                         net,
        IProgress<AnalysisProgressMessage>? progress = null,
        CancellationToken                   ct       = default);
}
```

**`IExportService.cs`**
```csharp
namespace PetriEditor.Shared.Contracts;

public interface IExportService
{
    Task<byte[]> ExportPdfAsync(ExportRequestDto request, CancellationToken ct = default);
    string GenerateTikZ(PetriNetDto net);
    string GeneratePnml(PetriNetDto net);
    PetriNetDto ParsePnml(string xml);
}
```

**`ISerializationService.cs`**
```csharp
namespace PetriEditor.Shared.Contracts;

public interface ISerializationService
{
    string SerializeToJson(PetriNetDto net);
    PetriNetDto DeserializeFromJson(string json);
    Task DownloadFileAsync(string fileName, string content, string mimeType);
    Task<string?> UploadFileAsync(string accept);
}
```

### Mapper — `PetriEditor.Shared/Mapping/PetriNetMapper.cs`

```csharp
namespace PetriEditor.Shared.Mapping;

public static class PetriNetMapper
{
    public static PetriNetSnapshot ToSnapshot(PetriNetDto dto) { ... }
}
```

### Verify
- `PetriEditor.Shared` builds standalone with zero errors
- No references to `Blazor`, `AspNetCore`, `Z.Blazor.Diagrams` anywhere in Shared

---

## Phase 3 — Server Setup

**Goal:** Stand up the ASP.NET Core host, SignalR hub with progress streaming and cancellation, and server-side service implementations.

### Update `AnalysisService` in `PetriEditor.Analysis`

Add `IProgress<AnalysisProgressMessage>` parameter to `RunAsync`. Report progress after each stage:

| Stage | Percent |
|---|---|
| StateSpace | 10 |
| Invariants | 30 |
| Classification | 50 |
| Cycles | 65 |
| TrapCotrap | 80 |
| ReachabilityTree | 88 |
| CoverabilityTree | 95 |
| PropertyTests | 98 |
| Complete | 100 |

### `PetriEditor.Server/Hubs/AnalysisHub.cs`

```csharp
namespace PetriEditor.Server.Hubs;

public sealed class AnalysisHub : Hub
{
    private readonly AnalysisOrchestrator _orchestrator;

    public AnalysisHub(AnalysisOrchestrator orchestrator)
    {
        _orchestrator = orchestrator;
    }

    public async Task<AnalysisResultDto> RunAnalysisAsync(PetriNetDto netDto, CancellationToken ct)
    {
        var progress = new Progress<AnalysisProgressMessage>(msg =>
            Clients.Caller.SendAsync("AnalysisProgress", msg, CancellationToken.None));

        return await _orchestrator.RunAsync(netDto, progress, ct);
    }

    public Task<byte[]> ExportPdfAsync(ExportRequestDto request, CancellationToken ct)
        => _orchestrator.ExportPdfAsync(request, ct);
}
```

### `PetriEditor.Server/Services/AnalysisOrchestrator.cs`

Thin wrapper that converts DTOs and delegates to `AnalysisService`:
```csharp
public sealed class AnalysisOrchestrator
{
    private readonly AnalysisService _engine;
    private readonly PdfExportService _pdf;

    public async Task<AnalysisResultDto> RunAsync(
        PetriNetDto net,
        IProgress<AnalysisProgressMessage>? progress,
        CancellationToken ct)
    {
        var snapshot = PetriNetMapper.ToSnapshot(net);
        var report = await _engine.RunAsync(snapshot, progress, ct);
        return AnalysisResultMapper.ToDto(report, net);
    }

    public Task<byte[]> ExportPdfAsync(ExportRequestDto request, CancellationToken ct)
        => Task.FromResult(_pdf.Generate(request));
}
```

### `PetriEditor.Server/Services/PdfExportService.cs`

Uses QuestPDF. Key sections:
- Net structure table (places, transitions, arcs)
- Analysis results section (property results with colored status badges)
- If reachability graph is small enough, include a summary table (not full graph — too large for PDF)

### `PetriEditor.Server/Mapping/AnalysisResultMapper.cs`

Converts `AnalysisReport` → `AnalysisResultDto`. Include reachability graph and coverability tree DTOs (populated once Phase 5 algorithms exist; stub with `null` in Phase 3).

### Update `PetriEditor.Server/Program.cs`

```csharp
builder.Services.AddSingleton<AnalysisService>();
builder.Services.AddSingleton<PdfExportService>();
builder.Services.AddScoped<AnalysisOrchestrator>();
builder.Services.AddSignalR(opts => {
    opts.MaximumReceiveMessageSize = 10 * 1024 * 1024; // 10MB for large nets
});
QuestPDF.Settings.License = LicenseType.Community;
```

### Verify
- Server starts, `/hubs/analysis` endpoint exists
- A simple SignalR test client can call `RunAnalysisAsync` and receive progress messages + final result

---

## Phase 4 — Client Setup (Blazor Auto Wiring)

**Goal:** Wire Blazor Auto render mode. Implement `ClientAnalysisService` using SignalR. Restore all existing editor functionality via new project structure.

### `PetriEditor.Client/Program.cs`

```csharp
var builder = WebAssemblyHostBuilder.CreateDefault(args);

builder.Services.AddSingleton<DiagramStateService>();
builder.Services.AddScoped<PetriNetManager>();
builder.Services.AddScoped<SimulationService>();
builder.Services.Configure<DiagramSettingsOptions>(...);
builder.Services.AddSingleton<DiagramSettings>();
builder.Services.AddScoped<IAnalysisService, ClientAnalysisService>();
builder.Services.AddScoped<IExportService, ClientExportService>();
builder.Services.AddScoped<ISerializationService, BrowserSerializationService>();
builder.Services.AddScoped<CytoscapeInterop>();
builder.Services.AddScoped<DiagramSerializer>();

await builder.Build().RunAsync();
```

### `PetriEditor.Client/Services/ClientAnalysisService.cs`

```csharp
public sealed class ClientAnalysisService : IAnalysisService, IAsyncDisposable
{
    private readonly HubConnection _hub;
    private IProgress<AnalysisProgressMessage>? _currentProgress;

    public ClientAnalysisService(NavigationManager nav)
    {
        _hub = new HubConnectionBuilder()
            .WithUrl(nav.ToAbsoluteUri("/hubs/analysis"))
            .WithAutomaticReconnect()
            .Build();

        _hub.On<AnalysisProgressMessage>("AnalysisProgress",
            msg => _currentProgress?.Report(msg));
    }

    public async Task<AnalysisResultDto> RunAnalysisAsync(
        PetriNetDto net,
        IProgress<AnalysisProgressMessage>? progress = null,
        CancellationToken ct = default)
    {
        if (_hub.State == HubConnectionState.Disconnected)
            await _hub.StartAsync(ct);

        _currentProgress = progress;
        try
        {
            return await _hub.InvokeAsync<AnalysisResultDto>("RunAnalysisAsync", net, ct);
        }
        finally
        {
            _currentProgress = null;
        }
    }

    public async ValueTask DisposeAsync() => await _hub.DisposeAsync();
}
```

### Render Mode Declarations

Add to `Home.razor`:
```razor
@rendermode InteractiveAuto
```

Add to `App.razor` in Server project:
```razor
<Routes @rendermode="InteractiveAuto" />
```

### Update `AnalysisPanel.razor`

Replace direct `AnalysisService` injection with `IAnalysisService`. Replace `RunAsync` call with `RunAnalysisAsync`. Add a progress bar that shows `AnalysisProgressMessage.Stage` and `Percent`.

### Snapshot Integration

`AnalysisPanel` currently builds a `PetriNetSnapshot` from the diagram. Replace this with building a `PetriNetDto` (using `DiagramSerializer.Capture()` from Phase 8 — stub it as a simple extraction in Phase 4 without full round-trip support).

### Verify
- App loads in browser with existing editor fully functional
- Analysis runs via SignalR (check browser Network tab for WS frames)
- Progress percentage shows during analysis
- Undo/redo still works

---

## Phase 5 — Reachability and Coverability Algorithms

**Goal:** Implement `ReachabilityTreeBuilder` and `CoverabilityTreeBuilder` in `PetriEditor.Analysis`. Wire into `AnalysisService`. Populate tree DTOs.

### `PetriEditor.Analysis/Algorithms/ReachabilityTreeBuilder.cs`

The existing `StateSpaceAnalysis` builds a reachability *graph* (deduplicates states). The tree is the BFS unrolling where already-visited markings are included but flagged as `IsDuplicate = true` (no further children from them).

```csharp
namespace PetriEditor.Analysis.Algorithms;

public sealed class ReachabilityTreeBuilder
{
    public const int MaxNodes = 100_000;

    public bool HasErrors { get; private set; }
    public string? ErrorMessage { get; private set; }
    public IReadOnlyList<ReachTreeNode> Nodes => _nodes;
    public IReadOnlyList<ReachTreeEdge> Edges => _edges;

    private readonly List<ReachTreeNode> _nodes = [];
    private readonly List<ReachTreeEdge> _edges = [];

    public void Build(
        PetriNetSnapshot net,
        IProgress<int>?  progress  = null,
        CancellationToken ct       = default)
    {
        // BFS from initial marking
        // Each node tracks its marking as int[] (one entry per place, in net.Places order)
        // Known markings stored in HashSet<string> (serialize marking to string key)
        // When a marking is already known: create node with IsDuplicate=true, no further expansion
        // Stop at MaxNodes and set HasErrors=true with error message
    }
}

public sealed record ReachTreeNode(
    int          Id,
    int[]        Marking,
    bool         IsInitial,
    bool         IsDeadlock,
    bool         IsDuplicate,
    int          ParentId);   // -1 for root

public sealed record ReachTreeEdge(int From, int To, string TransitionId, string TransitionName);
```

### `PetriEditor.Analysis/Algorithms/CoverabilityTreeBuilder.cs`

Karp-Miller algorithm for unbounded nets. Uses `int[]` with `Omega = int.MaxValue` as sentinel.

```csharp
public sealed class CoverabilityTreeBuilder
{
    public const int MaxNodes = 100_000;
    public const int Omega    = int.MaxValue;  // ω sentinel

    public bool HasErrors { get; private set; }
    public string? ErrorMessage { get; private set; }
    public IReadOnlyList<CoverTreeNode> Nodes => _nodes;
    public IReadOnlyList<CoverTreeEdge> Edges => _edges;

    private readonly List<CoverTreeNode> _nodes = [];
    private readonly List<CoverTreeEdge> _edges = [];

    // Karp-Miller procedure:
    // 1. Root = initial marking
    // 2. For each leaf node n with marking m, for each enabled transition t:
    //    a. Compute m' = Fire(t, m) treating Omega+anything = Omega
    //    b. Walk ancestor chain from n to root
    //       For any ancestor a with marking ma: if ma[i] <= m'[i] for all i,
    //       and ma[j] < m'[j] for some j: set m'[j] = Omega
    //    c. If m' equals a node already in the tree: create duplicate leaf, stop expanding
    //    d. Otherwise add new node with marking m', add edge n->new
    // 3. Stop at MaxNodes
    public void Build(
        PetriNetSnapshot net,
        IProgress<int>?  progress  = null,
        CancellationToken ct       = default) { ... }
}

public sealed record CoverTreeNode(
    int   Id,
    int[] Marking,     // int.MaxValue = Omega
    bool  IsInitial,
    bool  IsDeadlock,
    bool  IsDuplicate,
    int   ParentId);

public sealed record CoverTreeEdge(int From, int To, string TransitionId, string TransitionName);
```

### Update `AnalysisService.RunAsync`

After existing engines, add:
```csharp
var rtBuilder = new ReachabilityTreeBuilder();
rtBuilder.Build(net, progress: ..., ct);
report.ReachabilityTree = rtBuilder;

var ctBuilder = new CoverabilityTreeBuilder();
ctBuilder.Build(net, progress: ..., ct);
report.CoverabilityTree = ctBuilder;
```

Add `ReachabilityTree` and `CoverabilityTree` properties to `AnalysisReport`.

### Update `AnalysisResultMapper`

Map tree builder data → `ReachabilityGraphDto` / `CoverabilityTreeDto`. Map `int.MaxValue` → `null` (null = ω in the DTO).

### Verify
- Unit test: single-place net with one transition adding a token → CoverabilityTree root=[1], child=[ω]
- Unit test: simple 3-place bounded net → ReachabilityTree has expected node count
- Both trees appear non-null in `AnalysisResultDto` after a server analysis call

---

## Phase 6 — Tree/Graph Visualization (Cytoscape.js)

**Goal:** Render reachability graph and coverability tree as interactive diagrams in browser using Cytoscape.js.

### CDN Script

Add to `PetriEditor.Client/wwwroot/index.html` before closing `</body>`:
```html
<script src="https://cdnjs.cloudflare.com/ajax/libs/cytoscape/3.29.2/cytoscape.min.js"></script>
```

### `PetriEditor.Client/wwwroot/js/petriEditor.js` — Cytoscape Functions

```javascript
petriEditor.initCytoscape = function(containerId, elements, layoutName) {
    const container = document.getElementById(containerId);
    if (!container) return;

    petriEditor._cy = petriEditor._cy ?? {};
    petriEditor._cy[containerId]?.destroy();

    petriEditor._cy[containerId] = cytoscape({
        container,
        elements,
        style: [
            {
                selector: 'node',
                style: {
                    'label':              'data(label)',
                    'text-wrap':          'wrap',
                    'text-max-width':     '120px',
                    'font-size':          '11px',
                    'background-color':   '#e3f2fd',
                    'border-color':       '#1e88e5',
                    'border-width':       2,
                    'width':              'label',
                    'height':             'label',
                    'padding':            '8px',
                    'shape':              'roundrectangle'
                }
            },
            {
                selector: 'node.initial',
                style: { 'border-width': 4, 'border-color': '#2e7d32' }
            },
            {
                selector: 'node.deadlock',
                style: { 'background-color': '#ffebee', 'border-color': '#c62828' }
            },
            {
                selector: 'node.duplicate',
                style: { 'background-color': '#f3e5f5', 'border-color': '#7b1fa2', 'border-style': 'dashed' }
            },
            {
                selector: 'node.omega',
                style: { 'background-color': '#e8eaf6', 'border-color': '#3949ab' }
            },
            {
                selector: 'edge',
                style: {
                    'label':          'data(label)',
                    'font-size':      '10px',
                    'curve-style':    'bezier',
                    'target-arrow-shape': 'triangle',
                    'arrow-scale':    1.2,
                    'line-color':     '#90a4ae',
                    'target-arrow-color': '#90a4ae'
                }
            }
        ],
        layout: { name: layoutName ?? 'breadthfirst', directed: true, spacingFactor: 1.5 }
    });
};

petriEditor.destroyCytoscape = function(containerId) {
    petriEditor._cy?.[containerId]?.destroy();
    delete petriEditor._cy?.[containerId];
};

petriEditor.fitCytoscape = function(containerId) {
    petriEditor._cy?.[containerId]?.fit();
};
```

### `PetriEditor.Client/Services/CytoscapeInterop.cs`

```csharp
namespace PetriEditor.Client.Services;

public sealed class CytoscapeInterop
{
    private readonly IJSRuntime _js;
    public CytoscapeInterop(IJSRuntime js) { _js = js; }

    public ValueTask InitAsync(string containerId, IEnumerable<CyElement> elements, string layout = "breadthfirst")
        => _js.InvokeVoidAsync("petriEditor.initCytoscape", containerId, elements, layout);

    public ValueTask DestroyAsync(string containerId)
        => _js.InvokeVoidAsync("petriEditor.destroyCytoscape", containerId);

    public ValueTask FitAsync(string containerId)
        => _js.InvokeVoidAsync("petriEditor.fitCytoscape", containerId);
}

public sealed record CyElement(string Group, CyData Data, string[]? Classes = null);
public sealed record CyData(string Id, string? Label, string? Source, string? Target);
```

### `PetriEditor.Client/Mapping/CytoscapeMapper.cs`

```csharp
namespace PetriEditor.Client.Mapping;

public static class CytoscapeMapper
{
    public static IReadOnlyList<CyElement> FromReachabilityGraph(
        ReachabilityGraphDto graph,
        IReadOnlyList<string> placeNames)
    {
        var elements = new List<CyElement>();

        foreach (var node in graph.Nodes)
        {
            var label = MarkingLabel(node.Marking.Select(t => (int?)t).ToList(), placeNames);
            var classes = new List<string>();
            if (node.IsInitial)   classes.Add("initial");
            if (node.IsDeadlock)  classes.Add("deadlock");
            if (node.IsDuplicate) classes.Add("duplicate");

            elements.Add(new CyElement("nodes",
                new CyData(node.Id.ToString(), label, null, null),
                classes.Count > 0 ? classes.ToArray() : null));
        }

        foreach (var edge in graph.Edges)
            elements.Add(new CyElement("edges",
                new CyData($"e{edge.From}_{edge.To}", edge.TransitionName,
                    edge.From.ToString(), edge.To.ToString())));

        return elements;
    }

    public static IReadOnlyList<CyElement> FromCoverabilityTree(
        CoverabilityTreeDto tree,
        IReadOnlyList<string> placeNames)
    {
        var elements = new List<CyElement>();

        foreach (var node in tree.Nodes)
        {
            var label = MarkingLabel(node.Marking, placeNames);
            var classes = new List<string>();
            if (node.IsInitial)                              classes.Add("initial");
            if (node.IsDeadlock)                             classes.Add("deadlock");
            if (node.IsDuplicate)                            classes.Add("duplicate");
            if (node.Marking.Any(m => m is null))            classes.Add("omega");

            elements.Add(new CyElement("nodes",
                new CyData(node.Id.ToString(), label, null, null),
                classes.Count > 0 ? classes.ToArray() : null));
        }

        foreach (var edge in tree.Edges)
            elements.Add(new CyElement("edges",
                new CyData($"e{edge.From}_{edge.To}", edge.TransitionName,
                    edge.From.ToString(), edge.To.ToString())));

        return elements;
    }

    private static string MarkingLabel(IReadOnlyList<int?> marking, IReadOnlyList<string> placeNames)
    {
        var parts = new List<string>();
        for (int i = 0; i < marking.Count; i++)
        {
            var placeName = i < placeNames.Count ? placeNames[i] : $"p{i}";
            var tokenStr  = marking[i] is null ? "ω" : marking[i]!.Value.ToString();
            parts.Add($"{placeName}:{tokenStr}");
        }
        return string.Join(", ", parts);
    }
}
```

### New Components

**`PetriEditor.Client/Components/ReachabilityGraphView.razor`**

```razor
@implements IAsyncDisposable
@inject CytoscapeInterop Cy

<div style="display:flex;flex-direction:column;gap:8px;">
    <button class="btn btn-sm btn-outline-secondary" @onclick="FitGraph">Fit to screen</button>
    <div id="@_containerId" style="width:100%;height:500px;border:1px solid #dee2e6;border-radius:6px;"></div>
</div>

@code {
    [Parameter] public ReachabilityGraphDto? Graph { get; set; }
    [Parameter] public IReadOnlyList<string> PlaceNames { get; set; } = [];

    private readonly string _containerId = $"cy-reach-{Guid.NewGuid():N}";

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (Graph is null) return;
        var elements = CytoscapeMapper.FromReachabilityGraph(Graph, PlaceNames);
        await Cy.InitAsync(_containerId, elements);
    }

    private async Task FitGraph() => await Cy.FitAsync(_containerId);

    public async ValueTask DisposeAsync() => await Cy.DestroyAsync(_containerId);
}
```

**`PetriEditor.Client/Components/CoverabilityTreeView.razor`** — Same pattern using `CoverabilityTreeDto`.

### Update `AnalysisPanel.razor`

Add two new tabs alongside existing analysis results:
- "Reach. Graph" — renders `<ReachabilityGraphView>`
- "Cover. Tree" — renders `<CoverabilityTreeView>`

Pass place names from the current net to each component.

### Verify
- Simple bounded net: "Reach. Graph" tab shows Cytoscape graph, initial node highlighted, deadlock nodes highlighted
- Unbounded net: "Cover. Tree" tab shows omega nodes in blue/purple
- Fit button recenters the graph
- Switching tabs and running analysis again does not leave zombie Cytoscape instances

---

## Phase 7 — Export Features

**Goal:** PDF (server via SignalR), TikZ (browser), PNML import/export (browser).

### PDF — Server-Side

`PdfExportService.Generate(ExportRequestDto)` using QuestPDF:

```
Document
└── Page (A4)
    ├── Header: document title
    ├── Content
    │   ├── "Net Structure" section
    │   │   ├── Places table (name, initial tokens)
    │   │   ├── Transitions table (name, priority)
    │   │   └── Arcs table (source, target, weight, type)
    │   └── "Analysis Results" section (if IncludeAnalysis)
    │       ├── Classification summary
    │       ├── State count
    │       └── Property results table (property, status, reasons)
    └── Footer: page number
```

### TikZ — Client-Side

`PetriEditor.Client/Services/TikZGenerator.cs`:

```csharp
public static string Generate(PetriNetDto net)
{
    // Produces LaTeX using tikz + petri packages
    // Scale positions: divide by 50 to convert pixel coords to cm
    // Places: \node[place,tokens=N] (id) at (x,y) {Name};
    // Transitions: \node[transition] (id) at (x,y) {Name};  
    // Normal arcs: \draw[post] (source) to (target);
    // Weighted arcs: \draw[post,label={above:W}] (source) to (target);
    // Inhibitor arcs: \draw[inhibitor] (source) to (target);
    // Output wrapped in standalone LaTeX document preamble
}
```

Full output example:
```latex
\documentclass{standalone}
\usepackage{tikz}
\usetikzlibrary{petri,positioning,arrows}
\begin{document}
\begin{tikzpicture}[>=stealth',bend angle=45,auto]
  \node[place,tokens=1] (p1) at (1.2,0.4) {P1};
  \node[transition] (t1) at (2.6,0.4) {T1};
  \draw[post] (p1) to (t1);
\end{tikzpicture}
\end{document}
```

### PNML — Client-Side

`PetriEditor.Client/Services/PnmlSerializer.cs`:

```csharp
// Produces PNML 1.3.2 (P/T net standard):
// <pnml>
//   <net type="http://www.pnml.org/version-2009/grammar/ptnet" id="net1">
//     <place id="p1">
//       <name><text>P1</text></name>
//       <initialMarking><text>1</text></initialMarking>
//     </place>
//     <transition id="t1"><name><text>T1</text></name></transition>
//     <arc id="a1" source="p1" target="t1">
//       <inscription><text>1</text></inscription>
//     </arc>
//   </net>
// </pnml>
```

Inhibitor arcs: add `<type value="inhibitorArc"/>` child element.
Reset arcs: add `<type value="resetArc"/>` child element.

Position extension (optional, for round-trip): 
```xml
<graphics><position x="120" y="40"/></graphics>
```

For `ParsePnml`: read the `<net>` element, extract places/transitions/arcs. Read `<graphics><position>` if present for X/Y.

### Export Buttons in UI

Add an "Export" collapsible section to `AnalysisPanel.razor` (always visible, not just after analysis):

```
[Save JSON]  [Load JSON]   (file ops — Phase 8)
[Export TikZ] [Export PNML]
[PDF Report (no analysis)]  [PDF Report (with analysis)]  ← only if analysis ran
```

### Verify
- Downloaded `.tex` file compiles in LaTeX (pdflatex) without errors
- Downloaded `.pnml` file loads in WoPeD or similar tool without errors
- PDF report opens, shows correct structure and analysis results

---

## Phase 8 — Save / Load

**Goal:** Full round-trip for JSON save/load (all diagram state) and PNML import (structural only). No server storage — browser download/upload only.

### `PetriEditor.Client/wwwroot/js/petriEditor.js` — File I/O Functions

```javascript
petriEditor.downloadFile = function(fileName, content, mimeType) {
    const blob = new Blob([content], { type: mimeType });
    const url  = URL.createObjectURL(blob);
    const a    = document.createElement('a');
    a.href     = url;
    a.download = fileName;
    a.click();
    setTimeout(() => URL.revokeObjectURL(url), 1000);
};

petriEditor.uploadFile = function(accept) {
    return new Promise((resolve) => {
        const input = document.createElement('input');
        input.type   = 'file';
        input.accept = accept ?? '*/*';
        input.onchange = e => {
            const file = e.target.files[0];
            if (!file) { resolve(null); return; }
            const reader = new FileReader();
            reader.onload  = ev => resolve(ev.target.result);
            reader.onerror = ()  => resolve(null);
            reader.readAsText(file, 'utf-8');
        };
        input.click();
    });
};
```

### `PetriEditor.Client/Services/BrowserSerializationService.cs`

```csharp
public sealed class BrowserSerializationService : ISerializationService
{
    private readonly IJSRuntime _js;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented    = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public BrowserSerializationService(IJSRuntime js) { _js = js; }

    public string SerializeToJson(PetriNetDto net)
        => JsonSerializer.Serialize(net, JsonOptions);

    public PetriNetDto DeserializeFromJson(string json)
        => JsonSerializer.Deserialize<PetriNetDto>(json, JsonOptions)!;

    public Task DownloadFileAsync(string fileName, string content, string mimeType)
        => _js.InvokeVoidAsync("petriEditor.downloadFile", fileName, content, mimeType).AsTask();

    public async Task<string?> UploadFileAsync(string accept)
        => await _js.InvokeAsync<string?>("petriEditor.uploadFile", accept);
}
```

### `PetriEditor.Client/Services/DiagramSerializer.cs`

Captures the live diagram state into a `PetriNetDto` and restores it. Must handle:
- X/Y positions of nodes
- Arc vertices (bend points)
- Token counts, priorities, arc weights, arc types
- Clearing undo/redo history on load (loaded state is the new baseline)

```csharp
public sealed class DiagramSerializer
{
    private readonly PetriNetManager _manager;
    private readonly DiagramSettings _settings;

    public DiagramSerializer(PetriNetManager manager, DiagramSettings settings)
    {
        _manager  = manager;
        _settings = settings;
    }

    public PetriNetDto Capture()
    {
        var places = _manager.Diagram.Nodes
            .OfType<PlaceNode>()
            .Select(n => new PlaceDto(
                n.Data.Id, n.Data.Name, n.Data.Tokens,
                n.Position?.X ?? 0, n.Position?.Y ?? 0))
            .ToList();

        var transitions = _manager.Diagram.Nodes
            .OfType<TransitionNode>()
            .Select(n => new TransitionDto(
                n.Data.Id, n.Data.Name, n.Data.Priority,
                n.Position?.X ?? 0, n.Position?.Y ?? 0))
            .ToList();

        var arcs = _manager.Diagram.Links
            .OfType<PetriLinkModel>()
            .Where(l => l.Target is not null)
            .Select(l => new ArcDto(
                SourceId:  GetNodeId(l.SourceNode),
                TargetId:  GetNodeId(l.TargetNode),
                Weight:    l.Weight,
                ArcType:   (ArcType)l.ArcType,
                Vertices:  l.Vertices.Select(v => new PointDto(v.X, v.Y)).ToList()))
            .ToList();

        return new PetriNetDto(places, transitions, arcs);
    }

    public void Restore(PetriNetDto dto)
    {
        _manager.Diagram.Nodes.Clear();
        _manager.Diagram.Links.Clear();
        _manager.History.Clear();

        var nodeRegistry = new Dictionary<string, BaseDiagramNode>();

        foreach (var p in dto.Places)
        {
            var place = new Place { Id = p.Id, Name = p.Name, Tokens = p.Tokens };
            var node  = _manager.CreatePlaceNode(place);
            node.SetPosition(new Point(p.X, p.Y));
            nodeRegistry[p.Id] = node;
        }

        foreach (var t in dto.Transitions)
        {
            var trans = new Transition { Id = t.Id, Name = t.Name, Priority = t.Priority };
            var node  = _manager.CreateTransitionNode(trans);
            node.SetPosition(new Point(t.X, t.Y));
            nodeRegistry[t.Id] = node;
        }

        foreach (var a in dto.Arcs)
        {
            if (!nodeRegistry.TryGetValue(a.SourceId, out var source)) continue;
            if (!nodeRegistry.TryGetValue(a.TargetId, out var target)) continue;
            _manager.RestoreLink(source, target, a.Weight, (DiagramArcType)a.ArcType,
                a.Vertices.Select(v => new Point(v.X, v.Y)));
        }
    }
}
```

### Toolbar Buttons

Add to `Home.razor` toolbar area (next to existing undo/redo):
```
| New | Save | Load | ─────────── | [existing tools] |
```

New = confirm dialog ("Unsaved changes will be lost. Continue?") then clear diagram.
Save = `Capture()` → `SerializeToJson()` → `DownloadFileAsync("net.json", ...)`
Load = `UploadFileAsync(".json,.pnml")` → detect format by content → `DeserializeFromJson()` or `ParsePnml()` → `Restore()`

### Verify
- Draw a net with places, transitions, arcs with bend points. Save JSON. Reload page. Load JSON. Diagram restores exactly.
- Export as PNML. Load PNML. Places, transitions, arcs restore (no bend points — that is expected).
- Undo/redo history is empty after load (no crash when pressing Ctrl+Z immediately after load).
- "New" button clears everything cleanly.

---

## Global Notes

### SignalR Cancellation
When the browser tab closes or user clicks Cancel, the `CancellationToken` passed to `RunAnalysisAsync` fires. `AnalysisService.RunAsync` should call `ct.ThrowIfCancellationRequested()` between each major stage.

### Computation Location Toggle (Future Extension Point)
If a computation-location toggle is ever added, the seam is `IAnalysisService`. `ClientAnalysisService` calls server. A future `LocalAnalysisService` would call the analysis engine directly in WASM (import `PetriEditor.Analysis` into the client project). Only `Program.cs` needs to change to register the different implementation.

### Error Handling
Analysis errors should be surfaced via `AnalysisProgressMessage` with `Stage = "Error"` and non-null `ErrorText`. The UI should display the error text inline in the analysis panel instead of crashing.

### File Size Limits
SignalR max message size is set to 10MB in Phase 3. For extremely large reachability graphs (near 100k nodes), consider streaming the graph data in chunks rather than returning it all in one `AnalysisResultDto`. This is a future optimization — not needed for Phase 5.
