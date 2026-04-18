using Microsoft.AspNetCore.DataProtection;
using PetriEditor.Server.Analysis;
using PetriEditor.Server.Hubs;
using PetriEditor.Server.Logging;
using PetriEditor.Server.Services;
using QuestPDF.Infrastructure;

// QuestPDF community license — free for open-source / educational use
QuestPDF.Settings.License = LicenseType.Community;

// On low-memory hosts, trim the heap back after large analysis runs
if (Environment.GetEnvironmentVariable("RAILWAY_ENVIRONMENT") != null ||
    Environment.GetEnvironmentVariable("LOW_MEMORY_GC") != null)
{
    System.Runtime.GCSettings.LatencyMode = System.Runtime.GCLatencyMode.Batch;
}

var builder = WebApplication.CreateBuilder(args);

// File logger — writes warnings and errors to logs/petri.log next to the executable
var logPath = Path.Combine(AppContext.BaseDirectory, "logs", "petri.log");
builder.Logging.AddProvider(new FileLoggerProvider(logPath));

// Suppress noisy framework warnings that aren't actionable
builder.Logging.AddFilter("Microsoft.AspNetCore.DataProtection", LogLevel.Error);
builder.Logging.AddFilter("Microsoft.AspNetCore.Antiforgery", LogLevel.Error);
builder.Logging.AddFilter("Microsoft.AspNetCore.HttpsPolicy", LogLevel.Error);
builder.Logging.AddFilter("Microsoft.AspNetCore.StaticFiles", LogLevel.Warning);

// Keep Data Protection keys stable across container restarts so antiforgery tokens
// from existing browser sessions remain valid after a redeploy. Override the path
// via DP_KEYS_PATH on hosts with non-default layouts (e.g. ephemeral containers).
var keysPath = Environment.GetEnvironmentVariable("DP_KEYS_PATH")
               ?? Path.Combine(AppContext.BaseDirectory, "dataprotection-keys");
Directory.CreateDirectory(keysPath);
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(keysPath));

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents()
    .AddInteractiveWebAssemblyComponents();

builder.Services.AddSignalR(options =>
{
    options.MaximumReceiveMessageSize = 10 * 1024 * 1024; // 10 MB
    options.MaximumParallelInvocationsPerClient = 2; // allow CancelAnalysis while RunAnalysis is executing
});

// Analysis pipeline — AnalysisOrchestrator is Scoped because SignalR hubs are Scoped
builder.Services.AddSingleton<PdfExportService>();
builder.Services.AddScoped<AnalysisOrchestrator>();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseWebAssemblyDebugging();
}
else
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
    app.UseHttpsRedirection();
}
app.UseAntiforgery();
app.MapStaticAssets();

app.MapRazorComponents<PetriEditor.Server.Components.App>()
    .AddInteractiveServerRenderMode()
    .AddInteractiveWebAssemblyRenderMode()
    .AddAdditionalAssemblies(typeof(PetriEditor.Client._Imports).Assembly)
    .WithStaticAssets();

app.MapHub<AnalysisHub>("/hubs/analysis");

app.Run();
