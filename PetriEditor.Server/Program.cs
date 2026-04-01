using PetriEditor.Server.Analysis;
using PetriEditor.Server.Hubs;
using PetriEditor.Server.Services;
using QuestPDF.Infrastructure;

// QuestPDF community license — free for open-source / educational use
QuestPDF.Settings.License = LicenseType.Community;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents()
    .AddInteractiveWebAssemblyComponents();

builder.Services.AddSignalR(options =>
{
    options.MaximumReceiveMessageSize = 10 * 1024 * 1024; // 10 MB
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
}

app.UseHttpsRedirection();
app.UseAntiforgery();
app.MapStaticAssets();

app.MapRazorComponents<PetriEditor.Server.Components.App>()
    .AddInteractiveServerRenderMode()
    .AddInteractiveWebAssemblyRenderMode()
    .AddAdditionalAssemblies(typeof(PetriEditor.Client._Imports).Assembly)
    .WithStaticAssets();

app.MapHub<AnalysisHub>("/hubs/analysis");

app.Run();
