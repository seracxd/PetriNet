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
    /// <paramref name="maxMarkings"/> caps how many markings the server explores
    /// during state-space / coverability construction. Clamped to
    /// <see cref="AnalysisLimits.MaxMarkings"/>.
    /// </summary>
    Task<AnalysisResultDto> RunAnalysisAsync(
        PetriNetDto                          net,
        int                                  maxMarkings,
        IProgress<AnalysisProgressMessage>?  progress = null,
        CancellationToken                    ct       = default);

    /// <summary>
    /// Compute only the graph/tree on demand using the coverability tree algorithm.
    /// Always terminates; for bounded nets the result has no ω tokens.
    /// <paramref name="maxMarkings"/> caps how many markings the server explores;
    /// the same value is used for the client-side display filter so the user sees
    /// exactly what they asked for.
    /// </summary>
    Task<GraphResultDto> ComputeGraphAsync(
        PetriNetDto       net,
        int               maxMarkings,
        CancellationToken ct = default);
}
