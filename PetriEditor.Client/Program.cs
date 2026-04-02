using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using PetriEditor.Client;
using PetriEditor.Client.Services;
using PetriEditor.Shared.Contracts;
using PetriNetAnalyzer.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);

// No RootComponents here — Blazor Auto renders via Server's App.razor.
// These are registered for standalone WASM fallback if needed.
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// ── Diagram / editor services ─────────────────────────────────────────────
builder.Services.AddSingleton<DiagramStateService>();
builder.Services.AddScoped<PetriNetManager>();
builder.Services.AddScoped<SimulationService>();
builder.Services.Configure<DiagramSettingsOptions>(
    builder.Configuration.GetSection(DiagramSettingsOptions.Section));
builder.Services.AddSingleton<DiagramSettings>();
builder.Services.AddSingleton<IDiagramLogger, AspNetDiagramLogger>();

// ── Cytoscape visualization ───────────────────────────────────────────────
builder.Services.AddScoped<CytoscapeInterop>();
builder.Services.AddScoped<BrowserFileService>();

// ── Analysis ──────────────────────────────────────────────────────────────
// DiagramAnalyzer: builds DTOs from diagram state and runs local analysis
builder.Services.AddScoped<DiagramAnalyzer>();
// IAnalysisService: sends requests to server over SignalR (default)
builder.Services.AddScoped<ClientAnalysisService>();
builder.Services.AddScoped<IAnalysisService>(sp => sp.GetRequiredService<ClientAnalysisService>());

// ── Export & serialization ────────────────────────────────────────────────
builder.Services.AddScoped<IExportService, ClientExportService>();
builder.Services.AddScoped<ISerializationService, BrowserSerializationService>();
builder.Services.AddScoped<DiagramSerializer>();

await builder.Build().RunAsync();
