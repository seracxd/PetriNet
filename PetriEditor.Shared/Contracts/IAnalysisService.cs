namespace PetriEditor.Shared.Contracts;

/// <summary>
/// Abstraction over running a full Petri-net analysis.
/// Implemented by:
///   • ServerAnalysisService  (Client) — sends request over SignalR, receives streamed progress
///   • LocalAnalysisService   (Client) — runs the analysis engines directly in WASM
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
    /// Always terminates; for bounded nets the result has no ω tokens.
    /// </summary>
    Task<GraphResultDto> ComputeGraphAsync(
        PetriNetDto       net,
        CancellationToken ct        = default,
        int               maxStates = 500_000);
}
