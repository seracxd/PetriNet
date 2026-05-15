using System.Runtime.CompilerServices;
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
    public async Task RunAnalysis(PetriNetDto net, int maxMarkings = 0)
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

        // Two separate sources: clientCts is what CancelAnalysis() targets;
        // deadlineCts fires only on server-side timeout. Linking them lets us
        // tell which one tripped so we can surface a useful error to the user
        // instead of silently swallowing a timeout.
        var clientCts   = new CancellationTokenSource();
        var deadlineCts = new CancellationTokenSource(_analysisDeadline);
        var cts         = CancellationTokenSource.CreateLinkedTokenSource(clientCts.Token, deadlineCts.Token);
        _analysisCts[Context.ConnectionId] = clientCts;

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
            var result = await _orchestrator.RunAsync(net, maxMarkings, progress, cts.Token);
            // Send metadata first (no graph data) so the client can render the analysis panel immediately.
            // Graph/tree data is streamed as separate chunks to keep individual messages small.
            var stripped = result with { ReachabilityGraph = null, ReachabilityTree = null, CoverabilityTree = null };
            await caller.SendAsync("ReceiveResult", stripped);
            await SendGraphChunksAsync(caller, result, cts.Token);
        }
        catch (OperationCanceledException) when (deadlineCts.IsCancellationRequested)
        {
            // Server deadline tripped — surface a friendly message instead of a silent swallow.
            await caller.SendAsync("ReceiveError",
                $"Analysis timed out after {_analysisDeadline.TotalSeconds:F0} seconds. " +
                "Try a smaller net or simpler structure.");
        }
        catch (OperationCanceledException)
        {
            // Client-initiated cancel — they already know, no message needed.
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
            deadlineCts.Dispose();
            clientCts.Dispose();
        }
    }

    /// <summary>
    /// Stream the coverability tree in 200-node chunks so large results never form a single large message.
    /// The client reassembles chunks into a <see cref="GraphResultDto"/>.
    /// </summary>
    public async IAsyncEnumerable<GraphChunkDto> StreamGraph(
        PetriNetDto net,
        int maxMarkings,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (!TryThrottle(nameof(StreamGraph)))
            throw new HubException("Too many requests. Please wait before retrying.");
        ValidateNetSize(net);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_analysisDeadline);

        await _heavyOpGate.WaitAsync(cts.Token);
        GraphResultDto result;
        try
        {
            result = await _orchestrator.RunGraphAsync(net, maxMarkings, cts.Token);
        }
        catch (OperationCanceledException)
        {
            _heavyOpGate.Release();
            throw;
        }
        catch (Exception ex)
        {
            _heavyOpGate.Release();
            _logger.LogError(ex, "StreamGraph failed for connection {ConnectionId}", Context.ConnectionId);
            throw new HubException(ex.Message);
        }
        _heavyOpGate.Release();

        // Only fail if we have no usable tree. Truncation is not a failure — the
        // tree is valid up to the cap and the UI has dedicated "truncated" messaging.
        if (result.ErrorMessage is not null && result.CoverabilityTree is null
                                            && result.ReachabilityGraph is null
                                            && result.ReachabilityTree is null)
            throw new HubException(result.ErrorMessage);

        foreach (var chunk in BuildChunks(
            result.ReachabilityGraph, result.ReachabilityTree, result.CoverabilityTree, result.StateSpace))
            yield return chunk;
    }

    /// <summary>Generate a PDF report for the given net and return the bytes.</summary>
    public async Task<byte[]> ExportPdf(ExportRequestDto request)
    {
        if (!TryThrottle(nameof(ExportPdf)))
            throw new HubException("Too many requests. Please wait before retrying.");

        using var cts = new CancellationTokenSource(_analysisDeadline);
        await _heavyOpGate.WaitAsync(cts.Token);
        try
        {
            return await Task.Run(() => _pdfExport.Generate(request), cts.Token);
        }
        catch (OperationCanceledException)
        {
            throw new HubException("PDF export timed out.");
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

    // ── Graph chunking helpers ────────────────────────────────────────────

    private const int GraphChunkSize = 200;

    private static async Task SendGraphChunksAsync(
        IClientProxy caller, AnalysisResultDto result, CancellationToken ct)
    {
        foreach (var chunk in BuildChunks(
            result.ReachabilityGraph, result.ReachabilityTree, result.CoverabilityTree, null))
        {
            ct.ThrowIfCancellationRequested();
            await caller.SendAsync("ReceiveGraphChunk", chunk, ct);
        }
        await caller.SendAsync("ReceiveGraphDone", cancellationToken: ct);
    }

    private static IEnumerable<GraphChunkDto> BuildChunks(
        ReachabilityGraphDto? reachGraph,
        ReachabilityGraphDto? reachTree,
        CoverabilityTreeDto?  coverTree,
        StateSpaceSummaryDto? summary)
    {
        if (reachGraph is not null)
            foreach (var c in ReachChunks("rg", reachGraph, null)) yield return c;
        if (reachTree is not null)
            foreach (var c in ReachChunks("rt", reachTree, null)) yield return c;
        if (coverTree is not null)
            foreach (var c in CoverChunks("ct", coverTree, summary)) yield return c;
    }

    private static IEnumerable<GraphChunkDto> ReachChunks(
        string key, ReachabilityGraphDto g, StateSpaceSummaryDto? summary)
    {
        if (g.Nodes.Count == 0) yield break;
        int nc    = (int)Math.Ceiling(g.Nodes.Count / (double)GraphChunkSize);
        int total = nc + 1;
        for (int i = 0; i < nc; i++)
            yield return new GraphChunkDto(key, i, total,
                g.Nodes.Skip(i * GraphChunkSize).Take(GraphChunkSize).ToList(),
                null, null, null,
                i == 0 ? g.PlaceNames : null,
                i == 0 ? summary : null);
        yield return new GraphChunkDto(key, nc, total, null, g.Edges, null, null, null, null);
    }

    private static IEnumerable<GraphChunkDto> CoverChunks(
        string key, CoverabilityTreeDto g, StateSpaceSummaryDto? summary)
    {
        if (g.Nodes.Count == 0) yield break;
        int nc    = (int)Math.Ceiling(g.Nodes.Count / (double)GraphChunkSize);
        int total = nc + 1;
        for (int i = 0; i < nc; i++)
            yield return new GraphChunkDto(key, i, total,
                null, null,
                g.Nodes.Skip(i * GraphChunkSize).Take(GraphChunkSize).ToList(),
                null,
                i == 0 ? g.PlaceNames : null,
                i == 0 ? summary : null);
        yield return new GraphChunkDto(key, nc, total, null, null, null, g.Edges, null, null, g.GraphLayout);
    }

    // ── Lifecycle ─────────────────────────────────────────────────────────

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        if (_analysisCts.TryRemove(Context.ConnectionId, out var aCts))
        {
            aCts.Cancel();
            aCts.Dispose();
        }

        foreach (var key in _lastCallAt.Keys)
            if (key.StartsWith(Context.ConnectionId, StringComparison.Ordinal))
                _lastCallAt.TryRemove(key, out _);

        if (exception != null)
            _logger.LogWarning(exception, "Client {ConnectionId} disconnected with error", Context.ConnectionId);

        return base.OnDisconnectedAsync(exception);
    }
}
