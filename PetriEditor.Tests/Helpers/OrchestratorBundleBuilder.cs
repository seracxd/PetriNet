using Analysis;
using Analysis.Algorithms;
using Analysis.Engines;

namespace PetriEditor.Tests.Helpers;

/// <summary>
/// Builds an <see cref="AnalysisBundle"/> the same way the server-side
/// AnalysisOrchestrator does, so tests can exercise the same control flow
/// without taking a dependency on the Server project.
///
/// Mirrors the unbounded-detection logic exactly: if the coverability tree
/// contains an ω-node, invariants are skipped via
/// <see cref="InvariantAnalysis.SkipUnbounded"/> and the state space is
/// capped at <see cref="UnboundedStateCap"/>.
/// </summary>
internal static class OrchestratorBundleBuilder
{
    public const int UnboundedStateCap = 500;

    public static AnalysisBundle Build(PetriNetSnapshot net, int cap = 5_000)
    {
        bool hasSpecialArcs = net.Arcs.Any(a => a.ArcType != PnArcType.Normal);

        var cb = new CoverabilityTreeBuilder();
        cb.Build(net, default, cap, disableOmegaAcceleration: hasSpecialArcs);

        bool isUnbounded = cb.Nodes.Any(n =>
            n.Marking.Any(CoverabilityTreeBuilder.GrowsWithoutBound));
        bool stateSpaceTooLarge = isUnbounded || (hasSpecialArcs && cb.IsTruncated);

        var ss = new StateSpaceAnalysis();
        ss.Build(net, default, stateSpaceTooLarge ? Math.Min(UnboundedStateCap, cap) : cap);

        var inv = new InvariantAnalysis();
        if (hasSpecialArcs)
            inv.Skip("Net contains inhibitor or reset arcs — invariants based on the ordinary incidence matrix would be misleading.");
        else
            inv.Compute(net);

        var cls = new ClassificationAnalysis();
        cls.Compute(net);

        var cyc = new CyclesAnalysis();
        cyc.Compute(net);

        var tc = new TrapCotrapAnalysis();
        tc.Compute(net);

        return new AnalysisBundle
        {
            Net              = net,
            StateSpace       = ss,
            CoverabilityTree = cb,
            Invariants       = inv,
            Classification   = cls,
            Cycles           = cyc,
            TrapCotrap       = tc,
        };
    }
}
