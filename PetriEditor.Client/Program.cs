using System.Globalization;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using PetriEditor.Client;
using PetriEditor.Client.Services;
using PetriEditor.Shared.Contracts;
using PetriNetAnalyzer.Services;

// Force invariant culture for all formatting. SVG attributes require "." as the
// decimal separator, but Razor interpolation honors the current culture and
// Czech/German/etc. locales emit ",". Without this, browser-rendered SVG (e.g.
// width="19,99...") fails to parse and the Blazor renderer crashes.
CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;

var builder = WebAssemblyHostBuilder.CreateDefault(args);

// No RootComponents here — Blazor Auto renders via Server's App.razor.
// These are registered for standalone WASM fallback if needed.
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// ── Diagram / editor services ─────────────────────────────────────────────
builder.Services.AddScoped<DiagramStateService>();
builder.Services.AddScoped<PetriNetManager>();
builder.Services.AddScoped<SimulationService>();
builder.Services.Configure<DiagramSettingsOptions>(
    builder.Configuration.GetSection(DiagramSettingsOptions.Section));
builder.Services.AddScoped<DiagramSettings>();
builder.Services.AddScoped<IDiagramLogger, AspNetDiagramLogger>();

builder.Services.AddScoped<BrowserFileService>();

// ── Analysis ──────────────────────────────────────────────────────────────
// DiagramAnalyzer: builds PetriNetDto from diagram state.
builder.Services.AddScoped<DiagramAnalyzer>();
// ClientAnalysisService talks to the server over SignalR. There is no in-browser
// fallback — if the server is unreachable the UI surfaces an error.
builder.Services.AddScoped<ClientAnalysisService>();
builder.Services.AddScoped<IAnalysisService>(sp => sp.GetRequiredService<ClientAnalysisService>());

// ── Export & serialization ────────────────────────────────────────────────
builder.Services.AddScoped<IExportService, ClientExportService>();
builder.Services.AddScoped<ISerializationService, BrowserSerializationService>();
builder.Services.AddScoped<DiagramSerializer>();

await builder.Build().RunAsync();
