using Analysis.Engines;
using Microsoft.AspNetCore.SignalR;
using PetriEditor.Server.Analysis;
using PetriEditor.Server.Services;
using PetriEditor.Shared.Contracts;

namespace PetriEditor.Server.Hubs;

/// <summary>
/// SignalR hub that handles analysis requests from the client.
///
/// Protocol:
///   Client → Server:  RunAnalysis(PetriNetDto)
///   Server → Client:  ReceiveProgress(AnalysisProgressMessage)  — streamed during run
///   Server → Client:  ReceiveResult(AnalysisResultDto)          — sent once on completion
///   Server → Client:  ReceiveError(string)                      — sent on failure
///
/// Cancellation: the client calls CancelAnalysis() or disconnects;
/// the hub cancels the in-flight CancellationTokenSource.
/// </summary>
public sealed class AnalysisHub : Hub
{
    private readonly AnalysisOrchestrator _orchestrator;
    private readonly PdfExportService _pdfExport;
    private readonly ILogger<AnalysisHub> _logger;

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, CancellationTokenSource>
        _ctsByConnection = new();

    public AnalysisHub(AnalysisOrchestrator orchestrator, PdfExportService pdfExport, ILogger<AnalysisHub> logger)
    {
        _orchestrator = orchestrator;
        _pdfExport = pdfExport;
        _logger = logger;
    }

    // ── Client-callable methods ───────────────────────────────────────────

    /// <summary>
    /// Start a full analysis for the given net.
    /// Progress messages are pushed back to the caller via "ReceiveProgress".
    /// The final result is pushed via "ReceiveResult" (or "ReceiveError" on failure).
    /// </summary>
    public async Task RunAnalysis(PetriNetDto net)
    {
        // Cancel any previous run for this connection
        if (_ctsByConnection.TryRemove(Context.ConnectionId, out var oldCts))
        {
            oldCts.Cancel();
            oldCts.Dispose();
        }

        var cts = new CancellationTokenSource();
        _ctsByConnection[Context.ConnectionId] = cts;

        // Capture caller so we can push from inside Task.Run
        var caller = Clients.Caller;

        var progress = new Progress<AnalysisProgressMessage>(msg =>
        {
            _ = caller.SendAsync("ReceiveProgress", msg).ContinueWith(
                t => { /* swallow — client may have disconnected */ },
                TaskContinuationOptions.OnlyOnFaulted);
        });

        try
        {
            var result = await _orchestrator.RunAsync(net, progress, cts.Token);
            await caller.SendAsync("ReceiveResult", result);
        }
        catch (OperationCanceledException)
        {
            // Client triggered the cancellation and already handles it via its CancellationToken registration.
            // Sending anything here would arrive on a completed TCS and crash the progress callback.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RunAnalysis failed for connection {ConnectionId}", Context.ConnectionId);
            await caller.SendAsync("ReceiveError", ex.Message);
        }
        finally
        {
            _ctsByConnection.TryRemove(Context.ConnectionId, out _);
            cts.Dispose();
        }
    }

    /// <summary>Compute the coverability tree on demand (handles both bounded and unbounded nets).</summary>
    public async Task<GraphResultDto> RunGraphAnalysis(PetriNetDto net, int maxStates = StateSpaceAnalysis.MaxStates)
    {
        if (_ctsByConnection.TryRemove(Context.ConnectionId, out var oldCts))
        {
            oldCts.Cancel();
            oldCts.Dispose();
        }
        var cts = new CancellationTokenSource();
        _ctsByConnection[Context.ConnectionId] = cts;
        try
        {
            return await _orchestrator.RunGraphAsync(net, cts.Token, maxStates);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RunGraphAnalysis failed for connection {ConnectionId}", Context.ConnectionId);
            throw;
        }
        finally
        {
            _ctsByConnection.TryRemove(Context.ConnectionId, out _);
            cts.Dispose();
        }
    }

    /// <summary>Generate a PDF report for the given net and return the bytes.</summary>
    public Task<byte[]> ExportPdf(ExportRequestDto request)
    {
        try
        {
            var bytes = _pdfExport.Generate(request);
            return Task.FromResult(bytes);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PDF export failed for connection {ConnectionId}", Context.ConnectionId);
            throw;
        }
    }

    /// <summary>Cancel any in-flight analysis for the current connection.</summary>
    public Task CancelAnalysis()
    {
        if (_ctsByConnection.TryGetValue(Context.ConnectionId, out var cts))
            cts.Cancel();

        return Task.CompletedTask;
    }

    // ── Lifecycle ─────────────────────────────────────────────────────────

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        if (_ctsByConnection.TryRemove(Context.ConnectionId, out var cts))
        {
            cts.Cancel();
            cts.Dispose();
        }

        if (exception != null)
            _logger.LogWarning(exception, "Client {ConnectionId} disconnected with error", Context.ConnectionId);

        return base.OnDisconnectedAsync(exception);
    }
}
