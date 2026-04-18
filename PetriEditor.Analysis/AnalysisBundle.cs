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

    /// <summary>True when the net contains at least one inhibitor arc.</summary>
    public bool HasInhibitorArcs => Net.Arcs.Any(a => a.ArcType == PnArcType.Inhibitor);

    /// <summary>True when the net contains at least one reset arc.</summary>
    public bool HasResetArcs => Net.Arcs.Any(a => a.ArcType == PnArcType.Reset);

    /// <summary>True when the net is ordinary (all arcs are normal, unit-weight).</summary>
    public bool IsOrdinaryNet => !HasInhibitorArcs && !HasResetArcs;

    /// Results of other property tests (used by DeadlockFree which needs Liveness).
    public Dictionary<NetProperty, PropertyTestResult> PropertyResults { get; } = [];
}
