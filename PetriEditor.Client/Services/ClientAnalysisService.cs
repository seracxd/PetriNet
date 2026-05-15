using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.SignalR.Client;
using PetriEditor.Shared.Contracts;

namespace PetriEditor.Client.Services;

/// <summary>
/// Sends analysis requests to the server over SignalR and streams progress
/// messages back to the caller.
///
/// The hub sends:
///   "ReceiveProgress"    — <see cref="AnalysisProgressMessage"/> during the run
///   "ReceiveResult"      — <see cref="AnalysisResultDto"/> (graph fields nulled) on completion
///   "ReceiveGraphChunk"  — <see cref="GraphChunkDto"/> streaming the graph/tree data in batches
///   "ReceiveGraphDone"   — signals all graph chunks have been sent
///   "ReceiveError"       — string message on failure
/// </summary>
public sealed class ClientAnalysisService : IAnalysisService, IAsyncDisposable
{
    private readonly HubConnection _hub;

    public ClientAnalysisService(NavigationManager nav)
    {
        _hub = new HubConnectionBuilder()
            .WithUrl(nav.ToAbsoluteUri("/hubs/analysis"))
            .WithAutomaticReconnect()
            .Build();
    }

    public async Task<AnalysisResultDto> RunAnalysisAsync(
        PetriNetDto                          net,
        int                                  maxMarkings,
        IProgress<AnalysisProgressMessage>?  progress = null,
        CancellationToken                    ct       = default)
    {
        if (_hub.State == HubConnectionState.Disconnected)
            await _hub.StartAsync(ct);

        var resultTcs = new TaskCompletionSource<AnalysisResultDto>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        // Graph chunk accumulators — assembled into the final DTO when ReceiveGraphDone arrives.
        AnalysisResultDto? baseResult = null;
        var reachNodes  = new List<ReachNodeDto>();
        var reachEdges  = new List<ReachEdgeDto>();
        IReadOnlyList<string>? reachPlaces = null;
        var treeNodes   = new List<ReachNodeDto>();
        var treeEdges   = new List<ReachEdgeDto>();
        IReadOnlyList<string>? treePlaces  = null;
        var coverNodes  = new List<CoverNodeDto>();
        var coverEdges  = new List<CoverEdgeDto>();
        IReadOnlyList<string>? coverPlaces = null;
        GraphLayoutDto?        coverLayout = null;

        using var progressSub = _hub.On<AnalysisProgressMessage>("ReceiveProgress",
            msg => progress?.Report(msg));

        using var resultSub = _hub.On<AnalysisResultDto>("ReceiveResult",
            r => { baseResult = r; });

        using var graphChunkSub = _hub.On<GraphChunkDto>("ReceiveGraphChunk", chunk =>
        {
            switch (chunk.GraphKey)
            {
                case "rg":
                    if (chunk.PlaceNames != null) reachPlaces = chunk.PlaceNames;
                    if (chunk.ReachNodes != null) reachNodes.AddRange(chunk.ReachNodes);
                    if (chunk.ReachEdges != null) reachEdges.AddRange(chunk.ReachEdges);
                    break;
                case "rt":
                    if (chunk.PlaceNames != null) treePlaces  = chunk.PlaceNames;
                    if (chunk.ReachNodes != null) treeNodes.AddRange(chunk.ReachNodes);
                    if (chunk.ReachEdges != null) treeEdges.AddRange(chunk.ReachEdges);
                    break;
                case "ct":
                    if (chunk.PlaceNames  != null) coverPlaces = chunk.PlaceNames;
                    if (chunk.CoverNodes  != null) coverNodes.AddRange(chunk.CoverNodes);
                    if (chunk.CoverEdges  != null) coverEdges.AddRange(chunk.CoverEdges);
                    if (chunk.GraphLayout != null) coverLayout = chunk.GraphLayout;
                    break;
            }
        });

        using var graphDoneSub = _hub.On("ReceiveGraphDone", () =>
        {
            if (baseResult is null) return;
            var reachGraph = reachNodes.Count > 0
                ? new ReachabilityGraphDto(reachNodes, reachEdges, reachPlaces ?? [])
                : null;
            var reachTree  = treeNodes.Count  > 0
                ? new ReachabilityGraphDto(treeNodes,  treeEdges,  treePlaces  ?? [])
                : null;
            var coverTree  = coverNodes.Count > 0
                ? new CoverabilityTreeDto(coverNodes,  coverEdges, coverPlaces ?? [], coverLayout)
                : null;
            resultTcs.TrySetResult(baseResult with
            {
                ReachabilityGraph = reachGraph,
                ReachabilityTree  = reachTree,
                CoverabilityTree  = coverTree,
            });
        });

        using var errorSub = _hub.On<string>("ReceiveError",
            error => resultTcs.TrySetException(new InvalidOperationException(error)));

        await using var ctReg = ct.Register(() =>
        {
            _ = _hub.InvokeAsync("CancelAnalysis", CancellationToken.None);
            resultTcs.TrySetCanceled(ct);
        });

        _ = Task.Run(async () =>
        {
            try
            {
                await _hub.InvokeAsync("RunAnalysis", net, maxMarkings, CancellationToken.None);
            }
            catch (Exception ex)
            {
                resultTcs.TrySetException(ex);
            }
        }, CancellationToken.None);

        return await resultTcs.Task;
    }

    public async Task<GraphResultDto> ComputeGraphAsync(
        PetriNetDto       dto,
        int               maxMarkings,
        CancellationToken ct = default)
    {
        if (_hub.State == HubConnectionState.Disconnected)
            await _hub.StartAsync(ct);

        var coverNodes = new List<CoverNodeDto>();
        var coverEdges = new List<CoverEdgeDto>();
        IReadOnlyList<string>? placeNames = null;
        StateSpaceSummaryDto?  summary    = null;
        GraphLayoutDto?        layout     = null;

        try
        {
            await foreach (var chunk in _hub.StreamAsync<GraphChunkDto>(
                "StreamGraph", dto, maxMarkings, ct))
            {
                if (chunk.CoverNodes  != null) coverNodes.AddRange(chunk.CoverNodes);
                if (chunk.CoverEdges  != null) coverEdges.AddRange(chunk.CoverEdges);
                if (chunk.PlaceNames  != null) placeNames = chunk.PlaceNames;
                if (chunk.Summary     != null) summary    = chunk.Summary;
                if (chunk.GraphLayout != null) layout     = chunk.GraphLayout;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new GraphResultDto(null, null, null, ex.Message, null);
        }

        var coverTree = coverNodes.Count > 0
            ? new CoverabilityTreeDto(coverNodes, coverEdges, placeNames ?? [], layout)
            : null;

        return new GraphResultDto(null, null, coverTree, null, summary);
    }

    public async ValueTask DisposeAsync() => await _hub.DisposeAsync();
}
