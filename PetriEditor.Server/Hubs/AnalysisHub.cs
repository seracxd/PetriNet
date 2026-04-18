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
        _analysisCts = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, CancellationTokenSource>
        _graphCts = new();

    // Global cap on concurrent CPU-heavy operations to prevent resource exhaustion
    // when many clients request analysis at the same time.
    private static readonly SemaphoreSlim _heavyOpGate =
        new(Math.Max(2, Environment.ProcessorCount), Math.Max(2, Environment.ProcessorCount));

    // Per-connection last-call timestamps for each method name.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime>
        _lastCallAt = new();

    private static readonly TimeSpan _minCallInterval = TimeSpan.FromMilliseconds(250);

    // Approximate element-count budget. A hostile client can push a valid-but-enormous
    // DTO within the 10MB SignalR message cap; reject it before spending CPU on analysis.
    private const int MaxNetElements = 100_000;

    // Hard ceiling on how long a single analysis run may take end-to-end.
    private static readonly TimeSpan _analysisDeadline = TimeSpan.FromSeconds(60);

    private static void ValidateNetSize(PetriNetDto net)
    {
        int total = (net.Places?.Count ?? 0) + (net.Transitions?.Count ?? 0) + (net.Arcs?.Count ?? 0);
        if (total > MaxNetElements)
            throw new HubException($"Net too large: {total} elements exceeds the {MaxNetElements} limit.");
    }

    public AnalysisHub(AnalysisOrchestrator orchestrator, PdfExportService pdfExport, ILogger<AnalysisHub> logger)
    {
        _orchestrator = orchestrator;
        _pdfExport = pdfExport;
        _logger = logger;
    }

    // Reject calls that arrive faster than _minCallInterval from the same connection+method.
    private bool TryThrottle(string method)
    {
        var key = $"{Context.ConnectionId}:{method}";
        var now = DateTime.UtcNow;
        var prev = _lastCallAt.GetValueOrDefault(key);
        if (now - prev < _minCallInterval) return false;
        _lastCallAt[key] = now;
        return true;
    }

    // ── Client-callable methods ───────────────────────────────────────────

    /// <summary>
    /// Start a full analysis for the given net.
    /// Progress messages are pushed back to the caller via "ReceiveProgress".
    /// The final result is pushed via "ReceiveResult" (or "ReceiveError" on failure).
    /// </summary>
    public async Task RunAnalysis(PetriNetDto net)
    {
        if (!TryThrottle(nameof(RunAnalysis)))
            throw new HubException("Too many requests. Please wait before retrying.");

        ValidateNetSize(net);

        // Cancel any previous run for this connection
        if (_analysisCts.TryRemove(Context.ConnectionId, out var oldCts))
        {
            oldCts.Cancel();
            oldCts.Dispose();
        }

        var cts = new CancellationTokenSource(_analysisDeadline);
        _analysisCts[Context.ConnectionId] = cts;

        // Capture caller so we can push from inside Task.Run
        var caller = Clients.Caller;

        var progress = new Progress<AnalysisProgressMessage>(msg =>
        {
            _ = caller.SendAsync("ReceiveProgress", msg).ContinueWith(
                t => { /* swallow — client may have disconnected */ },
                TaskContinuationOptions.OnlyOnFaulted);
        });

        await _heavyOpGate.WaitAsync(cts.Token);
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
            _heavyOpGate.Release();
            _analysisCts.TryRemove(Context.ConnectionId, out _);
            cts.Dispose();
        }
    }

    /// <summary>Compute the coverability tree on demand (handles both bounded and unbounded nets).</summary>
    public async Task<GraphResultDto> RunGraphAnalysis(PetriNetDto net, int maxStates = StateSpaceAnalysis.MaxStates)
    {
        if (!TryThrottle(nameof(RunGraphAnalysis)))
            throw new HubException("Too many requests. Please wait before retrying.");

        ValidateNetSize(net);

        if (_graphCts.TryRemove(Context.ConnectionId, out var oldCts))
        {
            oldCts.Cancel();
            oldCts.Dispose();
        }
        var cts = new CancellationTokenSource(_analysisDeadline);
        _graphCts[Context.ConnectionId] = cts;
        await _heavyOpGate.WaitAsync(cts.Token);
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
            _heavyOpGate.Release();
            _graphCts.TryRemove(Context.ConnectionId, out _);
            cts.Dispose();
        }
    }

    /// <summary>Generate a PDF report for the given net and return the bytes.</summary>
    public async Task<byte[]> ExportPdf(ExportRequestDto request)
    {
        if (!TryThrottle(nameof(ExportPdf)))
            throw new HubException("Too many requests. Please wait before retrying.");

        await _heavyOpGate.WaitAsync();
        try
        {
            return await Task.Run(() => _pdfExport.Generate(request));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PDF export failed for connection {ConnectionId}", Context.ConnectionId);
            throw;
        }
        finally
        {
            _heavyOpGate.Release();
        }
    }

    /// <summary>Cancel any in-flight analysis for the current connection.</summary>
    public Task CancelAnalysis()
    {
        if (_analysisCts.TryGetValue(Context.ConnectionId, out var cts))
            cts.Cancel();

        return Task.CompletedTask;
    }

    // ── Lifecycle ─────────────────────────────────────────────────────────

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        if (_analysisCts.TryRemove(Context.ConnectionId, out var aCts))
        {
            aCts.Cancel();
            aCts.Dispose();
        }
        if (_graphCts.TryRemove(Context.ConnectionId, out var gCts))
        {
            gCts.Cancel();
            gCts.Dispose();
        }

        foreach (var key in _lastCallAt.Keys)
            if (key.StartsWith(Context.ConnectionId, StringComparison.Ordinal))
                _lastCallAt.TryRemove(key, out _);

        if (exception != null)
            _logger.LogWarning(exception, "Client {ConnectionId} disconnected with error", Context.ConnectionId);

        return base.OnDisconnectedAsync(exception);
    }
}
