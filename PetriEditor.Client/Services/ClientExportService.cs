using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.SignalR.Client;
using PetriEditor.Shared.Contracts;

namespace PetriEditor.Client.Services;

/// <summary>
/// Sends export requests to the server (PDF) or generates output locally (TikZ, PNML).
///
/// PDF is always produced server-side because QuestPDF does not run in WebAssembly.
/// TikZ and PNML are generated in the browser — no round-trip needed.
///
/// Full TikZ / PNML generation is implemented in Phase 7; the stubs here already
/// provide the correct interface so the rest of the app compiles unchanged.
/// </summary>
public sealed class ClientExportService : IExportService, IAsyncDisposable
{
    private readonly NavigationManager _nav;
    private HubConnection? _hub;

    public ClientExportService(NavigationManager nav)
    {
        _nav = nav;
    }

    public async Task<byte[]> ExportAsync(ExportRequestDto request, CancellationToken ct = default)
    {
        return request.Format switch
        {
            ExportFormat.Pdf  => await ExportPdfViaServerAsync(request, ct),
            ExportFormat.TikZ => GenerateTikZLocally(request),
            ExportFormat.Pnml => GeneratePnmlLocally(request),
            _                 => throw new NotSupportedException($"Export format {request.Format} is not supported.")
        };
    }

    // ── PDF — server round-trip via SignalR ───────────────────────────────

    private async Task<byte[]> ExportPdfViaServerAsync(ExportRequestDto request, CancellationToken ct)
    {
        var hub = await GetOrConnectHubAsync(ct);
        return await hub.InvokeAsync<byte[]>("ExportPdf", request, ct);
    }

    // ── TikZ / PNML — local generation ───────────────────────────────────

    private static byte[] GenerateTikZLocally(ExportRequestDto request)
    {
        var tikz = TikZGenerator.Generate(request.Net);
        return System.Text.Encoding.UTF8.GetBytes(tikz);
    }

    private static byte[] GeneratePnmlLocally(ExportRequestDto request)
    {
        var pnml = PnmlSerializer.Serialize(request.Net);
        return System.Text.Encoding.UTF8.GetBytes(pnml);
    }

    // ── Hub connection ────────────────────────────────────────────────────

    private async Task<HubConnection> GetOrConnectHubAsync(CancellationToken ct)
    {
        if (_hub == null)
        {
            _hub = new HubConnectionBuilder()
                .WithUrl(_nav.ToAbsoluteUri("/hubs/analysis"))
                .WithAutomaticReconnect()
                .Build();
        }

        if (_hub.State == HubConnectionState.Disconnected)
            await _hub.StartAsync(ct);

        return _hub;
    }

    public async ValueTask DisposeAsync()
    {
        if (_hub != null)
            await _hub.DisposeAsync();
    }
}
