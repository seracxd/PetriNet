using Analysis.Engines;

namespace Analysis;

/// <summary>All engine outputs passed to every property test.</summary>
public sealed class AnalysisBundle
{
    public PetriNetSnapshot Net { get; init; } = null!;

    public StateSpaceAnalysis?    StateSpace     { get; init; }
    public InvariantAnalysis?     Invariants     { get; init; }
    public ClassificationAnalysis? Classification { get; init; }
    public CyclesAnalysis?        Cycles         { get; init; }
    public TrapCotrapAnalysis?    TrapCotrap     { get; init; }

    /// Results of other property tests (used by DeadlockFree which needs Liveness).
    public Dictionary<NetProperty, PropertyTestResult> PropertyResults { get; } = [];
}
