using Analysis;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using PetriNetAnalyzer;
using PetriNetAnalyzer.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddScoped(sp => new HttpClient { BaseAddress = new Uri(builder.HostEnvironment.BaseAddress) });

builder.Services.AddSingleton<DiagramStateService>();
builder.Services.AddScoped<PetriNetManager>();
builder.Services.AddScoped<SimulationService>();
builder.Services.Configure<DiagramSettingsOptions>(builder.Configuration.GetSection(DiagramSettingsOptions.Section));
builder.Services.AddSingleton<DiagramSettings>();
builder.Services.AddSingleton<IDiagramLogger, AspNetDiagramLogger>();
builder.Services.AddScoped<AnalysisService>();

await builder.Build().RunAsync();