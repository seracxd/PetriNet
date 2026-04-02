using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.SignalR.Client;
using PetriEditor.Shared.Contracts;

namespace PetriEditor.Client.Services;

/// <summary>
/// Sends analysis requests to the server over SignalR and streams progress
/// messages back to the caller.
///
/// The hub sends:
///   "ReceiveProgress" — <see cref="AnalysisProgressMessage"/> during the run
///   "ReceiveResult"   — <see cref="AnalysisResultDto"/> on completion
///   "ReceiveError"    — string message on failure
/// </summary>
public sealed class ClientAnalysisService : IAnalysisService, IAsyncDisposable
{
    private readonly HubConnection _hub;
    private IProgress<AnalysisProgressMessage>? _currentProgress;

    public ClientAnalysisService(NavigationManager nav)
    {
        _hub = new HubConnectionBuilder()
            .WithUrl(nav.ToAbsoluteUri("/hubs/analysis"))
            .WithAutomaticReconnect()
            .Build();

        _hub.On<AnalysisProgressMessage>("ReceiveProgress",
            msg => _currentProgress?.Report(msg));
    }

    public async Task<AnalysisResultDto> RunAnalysisAsync(
        PetriNetDto                          net,
        IProgress<AnalysisProgressMessage>?  progress = null,
        CancellationToken                    ct       = default)
    {
        if (_hub.State == HubConnectionState.Disconnected)
            await _hub.StartAsync(ct);

        _currentProgress = progress;

        var resultTcs = new TaskCompletionSource<AnalysisResultDto>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        // Wire one-shot handlers for the result / error frames
        using var resultSub = _hub.On<AnalysisResultDto>("ReceiveResult",
            result => resultTcs.TrySetResult(result));

        using var errorSub = _hub.On<string>("ReceiveError",
            error => resultTcs.TrySetException(new InvalidOperationException(error)));

        // Register cancellation — ask the server to cancel too
        await using var ctReg = ct.Register(() =>
        {
            _ = _hub.InvokeAsync("CancelAnalysis", CancellationToken.None);
            resultTcs.TrySetCanceled(ct);
        });

        try
        {
            // Fire the analysis request (no return value — result comes via "ReceiveResult")
            await _hub.InvokeAsync("RunAnalysis", net, CancellationToken.None);
            return await resultTcs.Task;
        }
        finally
        {
            _currentProgress = null;
        }
    }

    public async Task<GraphResultDto> ComputeGraphAsync(
        PetriNetDto       net,
        bool              coverability,
        CancellationToken ct = default,
        int               maxStates = 500_000)
    {
        if (_hub.State == HubConnectionState.Disconnected)
            await _hub.StartAsync(ct);

        return await _hub.InvokeAsync<GraphResultDto>("RunGraphAnalysis", net, coverability, maxStates, ct);
    }

    public async ValueTask DisposeAsync() => await _hub.DisposeAsync();
}
