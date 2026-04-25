namespace PetriEditor.Shared.Contracts;

/// <summary>
/// Abstraction over running a full Petri-net analysis.
/// Implemented by <c>ClientAnalysisService</c>, which sends requests to the
/// server over SignalR.  There is no in-browser fallback — if the server is
/// unreachable the call throws and the UI surfaces an error.
/// </summary>
public interface IAnalysisService
{
    /// <summary>
    /// Run a full analysis for the given net.
    /// Progress is reported through <paramref name="progress"/> (may be null).
    /// The task completes with the full result or throws on error / cancellation.
    /// </summary>
    Task<AnalysisResultDto> RunAnalysisAsync(
        PetriNetDto                          net,
        IProgress<AnalysisProgressMessage>?  progress = null,
        CancellationToken                    ct       = default);

    /// <summary>
    /// Compute only the graph/tree on demand using the coverability tree algorithm.
    /// Always terminates; for bounded nets the result has no ω tokens.  The build
    /// cap is fixed at <see cref="AnalysisLimits.MaxMarkings"/> — clients choose
    /// how much of the result to display separately.
    /// </summary>
    Task<GraphResultDto> ComputeGraphAsync(
        PetriNetDto       net,
        CancellationToken ct = default);
}
