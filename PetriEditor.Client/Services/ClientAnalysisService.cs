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

    public ClientAnalysisService(NavigationManager nav)
    {
        _hub = new HubConnectionBuilder()
            .WithUrl(nav.ToAbsoluteUri("/hubs/analysis"))
            .WithAutomaticReconnect()
            .Build();
    }

    public async Task<AnalysisResultDto> RunAnalysisAsync(
        PetriNetDto                          net,
        IProgress<AnalysisProgressMessage>?  progress = null,
        CancellationToken                    ct       = default)
    {
        if (_hub.State == HubConnectionState.Disconnected)
            await _hub.StartAsync(ct);

        var resultTcs = new TaskCompletionSource<AnalysisResultDto>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        // Per-call subscriptions — each call gets its own progress/result/error sink.
        using var progressSub = _hub.On<AnalysisProgressMessage>("ReceiveProgress",
            msg => progress?.Report(msg));

        using var resultSub = _hub.On<AnalysisResultDto>("ReceiveResult",
            result => resultTcs.TrySetResult(result));

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
                await _hub.InvokeAsync("RunAnalysis", net, CancellationToken.None);
            }
            catch (Exception ex)
            {
                resultTcs.TrySetException(ex);
            }
        }, CancellationToken.None);

        return await resultTcs.Task;
    }

    public async Task<GraphResultDto> ComputeGraphAsync(
        PetriNetDto       net,
        CancellationToken ct        = default,
        int               maxStates = 500_000)
    {
        if (_hub.State == HubConnectionState.Disconnected)
            await _hub.StartAsync(ct);

        var resultTcs = new TaskCompletionSource<GraphResultDto>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        await using var ctReg = ct.Register(() =>
        {
            _ = _hub.InvokeAsync("CancelAnalysis", CancellationToken.None);
            resultTcs.TrySetCanceled(ct);
        });

        // Run on background task so cancel can interrupt the await
        _ = Task.Run(async () =>
        {
            try
            {
                var result = await _hub.InvokeAsync<GraphResultDto>("RunGraphAnalysis", net, maxStates, CancellationToken.None);
                resultTcs.TrySetResult(result);
            }
            catch (Exception ex)
            {
                resultTcs.TrySetException(ex);
            }
        }, CancellationToken.None);

        return await resultTcs.Task;
    }

    public async ValueTask DisposeAsync() => await _hub.DisposeAsync();
}
