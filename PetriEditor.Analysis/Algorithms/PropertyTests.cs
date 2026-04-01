using Analysis.Engines;

namespace Analysis.Algorithms;

// ─────────────────────────────────────────────────────────────────────────────
// Liveness
// ─────────────────────────────────────────────────────────────────────────────

public sealed class LivenessTest
{
    public PropertyTestResult Run(AnalysisBundle b)
    {
        var builder = new PropertyResultBuilder(NetProperty.Liveness);
        var ss  = DecideFromStateSpace(b, builder);
        var inv = DecideFromInvariants(b, builder);
        var cls = DecideFromClassification(b, builder);
        builder.SetStatus(TestResultStatusExtensions.LogicalOr(ss, inv, cls));
        return builder.Build();
    }

    private static TestResultStatus DecideFromStateSpace(AnalysisBundle b, PropertyResultBuilder r)
    {
        var ss = b.StateSpace;
        if (ss is null) { r.AddReason("State space analysis results are unavailable."); return TestResultStatus.Undecidable; }
        if (ss.HasErrors) { r.LogError(ss.ErrorMsg!); return TestResultStatus.Undecidable; }

        bool live = ss.IsLive(b.Net.Transitions.Count);
        r.AddReason(live
            ? "Each final SCC of the state space contains all transitions as arc labels. Net is live."
            : "Not all final SCCs contain every transition as arc labels. Net is not live.");
        return live ? TestResultStatus.Pass : TestResultStatus.Fail;
    }

    private static TestResultStatus DecideFromInvariants(AnalysisBundle b, PropertyResultBuilder r)
    {
        var inv = b.Invariants;
        if (inv is null) { r.AddReason("Invariant analysis results are unavailable."); return TestResultStatus.Undecidable; }
        if (inv.HasErrors) { r.LogError(inv.ErrorMsg!); return TestResultStatus.Undecidable; }

        // P-invariant check: invariant with y·M₀ ≤ 0 whose support touches transitions → not live
        var pStatus = TestResultStatus.Undecidable;
        foreach (var pinv in inv.PInvariants)
        {
            int system = pinv.Structure.Sum(kv =>
                kv.Value * (b.Net.PlaceById.TryGetValue(kv.Key, out var pl) ? pl.Tokens : 0));
            if (system <= 0 && pinv.Structure.Keys.Any(pid => b.Net.ConnectedTransitions(pid).Any()))
            {
                pStatus = TestResultStatus.Fail;
                r.AddReason("A P-invariant with system value ≤ 0 exists whose support contains a place connected to transitions. Net is not live.");
                break;
            }
        }
        if (pStatus == TestResultStatus.Undecidable)
            r.AddReason("No P-invariant condition determines liveness from this net.");

        // T-invariant check: all transitions must be covered (necessary condition)
        var coveredTrans = new HashSet<string>(inv.TInvariants.SelectMany(ti => ti.Structure.Keys));
        bool allCovered  = b.Net.Transitions.All(t => coveredTrans.Contains(t.Id));
        var tStatus = allCovered ? TestResultStatus.Undecidable : TestResultStatus.Fail;
        r.AddReason(allCovered
            ? "All transitions are covered by T-invariants (necessary but not sufficient for liveness)."
            : "Some transition is not covered by any T-invariant — a necessary condition for liveness fails.");

        return TestResultStatusExtensions.LogicalOr(pStatus, tStatus);
    }

    private static TestResultStatus DecideFromClassification(AnalysisBundle b, PropertyResultBuilder r)
    {
        var cls = b.Classification;
        if (cls is null) { r.AddReason("Classification results are unavailable."); return TestResultStatus.Undecidable; }
        if (cls.HasErrors) { r.LogError(cls.ErrorMsg!); return TestResultStatus.Undecidable; }

        return TestResultStatusExtensions.LogicalOr(
            DecideStateMachine(b, r, cls),
            DecideMarkedGraph(b, r, cls),
            DecidefreeChoice(b, r, cls));
    }

    private static TestResultStatus DecideStateMachine(AnalysisBundle b, PropertyResultBuilder r, ClassificationAnalysis cls)
    {
        if (!cls.IsOfType(NetSubclass.StateMachine))
        { r.AddReason("Net is not a State machine. Liveness cannot be decided from this."); return TestResultStatus.Undecidable; }

        int tokenSum = b.Net.Places.Sum(p => p.Tokens);
        if (tokenSum <= 0)
        { r.AddReason("Net is a State machine but initial marking has zero tokens."); return TestResultStatus.Undecidable; }

        bool sc = IsNetStronglyConnected(b.Net);
        r.AddReason(sc
            ? "Net is a strongly connected State machine with tokens > 0. Net is live."
            : "Net is a State machine with tokens > 0 but is not strongly connected. Cannot decide liveness.");
        return sc ? TestResultStatus.Pass : TestResultStatus.Undecidable;
    }

    private static TestResultStatus DecideMarkedGraph(AnalysisBundle b, PropertyResultBuilder r, ClassificationAnalysis cls)
    {
        if (!cls.IsOfType(NetSubclass.MarkedGraph))
        { r.AddReason("Net is not a Marked graph. Liveness cannot be decided from this."); return TestResultStatus.Undecidable; }

        var cyc = b.Cycles;
        if (cyc is null) { r.AddReason("Net is a Marked graph but cycle analysis is unavailable."); return TestResultStatus.Undecidable; }

        bool eachHasTokens = cyc.Cycles.All(c => c.TokensInCycle > 0);
        r.AddReason(eachHasTokens
            ? "Net is a Marked graph and every cycle contains ≥ 1 token. Net is live."
            : "Net is a Marked graph but some cycle contains no tokens. Cannot decide liveness.");
        return eachHasTokens ? TestResultStatus.Pass : TestResultStatus.Undecidable;
    }

    private static TestResultStatus DecidefreeChoice(AnalysisBundle b, PropertyResultBuilder r, ClassificationAnalysis cls)
    {
        if (!cls.IsOfType(NetSubclass.FreeChoice))
        { r.AddReason("Net is not a Free-choice net. Liveness cannot be decided from this."); return TestResultStatus.Undecidable; }

        var tc = b.TrapCotrap;
        if (tc is null) { r.AddReason("Net is Free-choice but trap/co-trap analysis is unavailable."); return TestResultStatus.Undecidable; }

        var trapsWithTokens = tc.Traps.Where(t => t.HasToken).ToList();
        if (!tc.Cotraps.Any())
        { r.AddReason("Net is Free-choice but has no co-traps. Cannot decide liveness."); return TestResultStatus.Undecidable; }

        bool ok = tc.Cotraps.All(ct => trapsWithTokens.Any(trap => ct.PlaceIds.IsSupersetOf(trap.PlaceIds)));
        r.AddReason(ok
            ? "Net is Free-choice and each co-trap contains a trap with ≥ 1 token. Net is live."
            : "Net is Free-choice but some co-trap contains no marked trap. Net is not live.");
        return ok ? TestResultStatus.Pass : TestResultStatus.Fail;
    }

    private static bool IsNetStronglyConnected(PetriNetSnapshot net)
    {
        var all = net.Places.Select(p => p.Id).Concat(net.Transitions.Select(t => t.Id)).ToList();
        if (all.Count == 0) return true;
        var fwd = all.ToDictionary(id => id, _ => new List<string>());
        foreach (var arc in net.Arcs.Where(a => a.ArcType == PnArcType.Normal))
            if (fwd.ContainsKey(arc.SourceId) && fwd.ContainsKey(arc.TargetId))
                fwd[arc.SourceId].Add(arc.TargetId);
        var bwd = all.ToDictionary(id => id, _ => new List<string>());
        foreach (var (v, ws) in fwd) foreach (var w in ws) bwd[w].Add(v);
        return Reach(all[0], all, fwd) && Reach(all[0], all, bwd);
    }

    private static bool Reach(string start, List<string> all, Dictionary<string, List<string>> adj)
    {
        var vis = new HashSet<string>(); var q = new Queue<string>();
        q.Enqueue(start); vis.Add(start);
        while (q.Count > 0) { var v = q.Dequeue(); foreach (var w in adj[v]) if (vis.Add(w)) q.Enqueue(w); }
        return all.All(vis.Contains);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Boundedness
// ─────────────────────────────────────────────────────────────────────────────

public sealed class BoundednessTest
{
    public PropertyTestResult Run(AnalysisBundle b)
    {
        var builder = new PropertyResultBuilder(NetProperty.Boundedness);
        builder.SetStatus(TestResultStatusExtensions.LogicalOr(
            DecideFromStateSpace(b, builder),
            DecideFromInvariants(b, builder),
            DecideFromClassification(b, builder)));
        return builder.Build();
    }

    private static TestResultStatus DecideFromStateSpace(AnalysisBundle b, PropertyResultBuilder r)
    {
        var ss = b.StateSpace;
        if (ss is null) { r.AddReason("State space analysis results are unavailable."); return TestResultStatus.Undecidable; }
        if (ss.HasErrors) { r.LogError(ss.ErrorMsg!); return TestResultStatus.Undecidable; }
        bool bounded = ss.IsBounded();
        r.AddReason(bounded
            ? "Token counts are finite at every reachable marking. Net is bounded."
            : "Token count in some place may grow to infinity. Net is unbounded.");
        return bounded ? TestResultStatus.Pass : TestResultStatus.Fail;
    }

    private static TestResultStatus DecideFromInvariants(AnalysisBundle b, PropertyResultBuilder r)
    {
        var inv = b.Invariants;
        if (inv is null) { r.AddReason("Invariant analysis results are unavailable."); return TestResultStatus.Undecidable; }
        if (inv.HasErrors) { r.LogError(inv.ErrorMsg!); return TestResultStatus.Undecidable; }
        var covered = new HashSet<string>(inv.PInvariants.SelectMany(pi => pi.Structure.Keys));
        bool bounded = b.Net.Places.All(p => covered.Contains(p.Id));
        r.AddReason(bounded
            ? "All places are covered by P-invariants. Assuming finite initial marking, net is bounded."
            : "Some place is not covered by any P-invariant. Boundedness cannot be decided from invariants alone.");
        return bounded ? TestResultStatus.Pass : TestResultStatus.Undecidable;
    }

    private static TestResultStatus DecideFromClassification(AnalysisBundle b, PropertyResultBuilder r)
    {
        var cls = b.Classification;
        if (cls is null) { r.AddReason("Classification results are unavailable."); return TestResultStatus.Undecidable; }
        if (cls.HasErrors) { r.LogError(cls.ErrorMsg!); return TestResultStatus.Undecidable; }
        bool sm = cls.IsOfType(NetSubclass.StateMachine);
        r.AddReason(sm ? "Net is a State machine, therefore it is bounded."
                       : "Net is not a State machine. Boundedness cannot be decided from classification alone.");
        return sm ? TestResultStatus.Pass : TestResultStatus.Undecidable;
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Safety
// ─────────────────────────────────────────────────────────────────────────────

public sealed class SafetyTest
{
    public PropertyTestResult Run(AnalysisBundle b)
    {
        var builder = new PropertyResultBuilder(NetProperty.Safety);
        builder.SetStatus(TestResultStatusExtensions.LogicalOr(
            DecideFromStateSpace(b, builder),
            DecideFromClassification(b, builder)));
        return builder.Build();
    }

    private static TestResultStatus DecideFromStateSpace(AnalysisBundle b, PropertyResultBuilder r)
    {
        var ss = b.StateSpace;
        if (ss is null) { r.AddReason("State space analysis results are unavailable."); return TestResultStatus.Undecidable; }
        if (ss.HasErrors) { r.LogError(ss.ErrorMsg!); return TestResultStatus.Undecidable; }
        bool safe = ss.IsSafe();
        r.AddReason(safe ? "All places have ≤ 1 token at every reachable marking. Net is safe."
                         : "Some place has > 1 token at some reachable marking. Net is unsafe.");
        return safe ? TestResultStatus.Pass : TestResultStatus.Fail;
    }

    private static TestResultStatus DecideFromClassification(AnalysisBundle b, PropertyResultBuilder r)
    {
        var cls = b.Classification;
        if (cls is null) { r.AddReason("Classification results are unavailable."); return TestResultStatus.Undecidable; }
        if (cls.HasErrors) { r.LogError(cls.ErrorMsg!); return TestResultStatus.Undecidable; }
        return TestResultStatusExtensions.LogicalOr(
            DecideStateMachine(b, r, cls),
            DecideMarkedGraph(b, r, cls));
    }

    private static TestResultStatus DecideStateMachine(AnalysisBundle b, PropertyResultBuilder r, ClassificationAnalysis cls)
    {
        if (!cls.IsOfType(NetSubclass.StateMachine))
        { r.AddReason("Net is not a State machine. Safety cannot be decided from this."); return TestResultStatus.Undecidable; }
        int sum = b.Net.Places.Sum(p => p.Tokens);
        r.AddReason(sum <= 1 ? "Net is a State machine with initial token sum ≤ 1. Net is safe."
                             : "Net is a State machine but initial token sum > 1. Net is unsafe.");
        return sum <= 1 ? TestResultStatus.Pass : TestResultStatus.Fail;
    }

    private static TestResultStatus DecideMarkedGraph(AnalysisBundle b, PropertyResultBuilder r, ClassificationAnalysis cls)
    {
        if (!cls.IsOfType(NetSubclass.MarkedGraph))
        { r.AddReason("Net is not a Marked graph. Safety cannot be decided from this."); return TestResultStatus.Undecidable; }
        var cyc = b.Cycles;
        if (cyc is null) { r.AddReason("Cycle analysis unavailable."); return TestResultStatus.Undecidable; }
        var cyclePlaces = new HashSet<string>(cyc.Cycles.SelectMany(c => c.PlaceIds));
        bool allCovered = b.Net.Places.All(p => cyclePlaces.Contains(p.Id));
        bool noOverload = cyc.Cycles.All(c => c.TokensInCycle <= 1);
        r.AddReason(allCovered && noOverload
            ? "Net is a Marked graph; every place is in a cycle with ≤ 1 token. Net is safe."
            : "Net is a Marked graph but some cycle has > 1 token or some place is not covered. Cannot guarantee safety.");
        return allCovered && noOverload ? TestResultStatus.Pass : TestResultStatus.Undecidable;
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Conservativeness
// ─────────────────────────────────────────────────────────────────────────────

public sealed class ConservativenessTest
{
    public PropertyTestResult Run(AnalysisBundle b)
    {
        var builder = new PropertyResultBuilder(NetProperty.Conservativeness);
        builder.SetStatus(TestResultStatusExtensions.LogicalOr(
            DecideFromInvariants(b, builder),
            DecideFromClassification(b, builder)));
        return builder.Build();
    }

    private static TestResultStatus DecideFromInvariants(AnalysisBundle b, PropertyResultBuilder r)
    {
        var inv = b.Invariants;
        if (inv is null) { r.AddReason("Invariant analysis results are unavailable."); return TestResultStatus.Undecidable; }
        if (inv.HasErrors) { r.LogError(inv.ErrorMsg!); return TestResultStatus.Undecidable; }
        var covered = new HashSet<string>(inv.PInvariants.SelectMany(pi => pi.Structure.Keys));
        bool conservative = b.Net.Places.All(p => covered.Contains(p.Id));
        r.AddReason(conservative
            ? "All places are covered by P-invariants. Net is conservative."
            : "Some place is not covered by any P-invariant. Net is not conservative.");
        return conservative ? TestResultStatus.Pass : TestResultStatus.Fail;
    }

    private static TestResultStatus DecideFromClassification(AnalysisBundle b, PropertyResultBuilder r)
    {
        var cls = b.Classification;
        if (cls is null) { r.AddReason("Classification results are unavailable."); return TestResultStatus.Undecidable; }
        if (cls.HasErrors) { r.LogError(cls.ErrorMsg!); return TestResultStatus.Undecidable; }
        bool sm = cls.IsOfType(NetSubclass.StateMachine);
        r.AddReason(sm ? "Net is a State machine, therefore it is conservative."
                       : "Net is not a State machine. Conservativeness cannot be decided from classification alone.");
        return sm ? TestResultStatus.Pass : TestResultStatus.Undecidable;
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Repetitiveness
// ─────────────────────────────────────────────────────────────────────────────

public sealed class RepetitivenessTest
{
    public PropertyTestResult Run(AnalysisBundle b)
    {
        var builder = new PropertyResultBuilder(NetProperty.Repetitiveness);
        builder.SetStatus(DecideFromInvariants(b, builder));
        return builder.Build();
    }

    private static TestResultStatus DecideFromInvariants(AnalysisBundle b, PropertyResultBuilder r)
    {
        var inv = b.Invariants;
        if (inv is null) { r.AddReason("Invariant analysis results are unavailable."); return TestResultStatus.Undecidable; }
        if (inv.HasErrors) { r.LogError(inv.ErrorMsg!); return TestResultStatus.Undecidable; }
        var covered = new HashSet<string>(inv.TInvariants.SelectMany(ti => ti.Structure.Keys));
        bool repetitive = b.Net.Transitions.All(t => covered.Contains(t.Id));
        r.AddReason(repetitive
            ? "All transitions are covered by T-invariants. Net is repetitive."
            : "Some transition is not covered by any T-invariant. Net is not repetitive.");
        return repetitive ? TestResultStatus.Pass : TestResultStatus.Fail;
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Deadlock-Free
// ─────────────────────────────────────────────────────────────────────────────

public sealed class DeadlockFreeTest
{
    public PropertyTestResult Run(AnalysisBundle b)
    {
        var builder = new PropertyResultBuilder(NetProperty.DeadlockFree);
        builder.SetStatus(TestResultStatusExtensions.LogicalOr(
            DecideFromStateSpace(b, builder),
            DecideFromLiveness(b, builder)));
        return builder.Build();
    }

    private static TestResultStatus DecideFromStateSpace(AnalysisBundle b, PropertyResultBuilder r)
    {
        var ss = b.StateSpace;
        if (ss is null) { r.AddReason("State space analysis results are unavailable."); return TestResultStatus.Undecidable; }
        if (ss.HasErrors) { r.LogError(ss.ErrorMsg!); return TestResultStatus.Undecidable; }
        bool df = ss.IsDeadlockFree();
        r.AddReason(df ? "Every state space node has ≥ 1 successor. Net is deadlock-free."
                       : "Some state space node has no successors. Net is not deadlock-free.");
        return df ? TestResultStatus.Pass : TestResultStatus.Fail;
    }

    private static TestResultStatus DecideFromLiveness(AnalysisBundle b, PropertyResultBuilder r)
    {
        if (!b.PropertyResults.TryGetValue(NetProperty.Liveness, out var liveness))
        { r.AddReason("Liveness test results are unavailable."); return TestResultStatus.Undecidable; }
        bool live = liveness.Status == TestResultStatus.Pass;
        r.AddReason(live ? "Net is live, therefore it is deadlock-free."
                         : "Net is not live or liveness is undecidable. Cannot decide deadlock-freedom from liveness alone.");
        return live ? TestResultStatus.Pass : TestResultStatus.Undecidable;
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Reversibility
// ─────────────────────────────────────────────────────────────────────────────

public sealed class ReversibilityTest
{
    public PropertyTestResult Run(AnalysisBundle b)
    {
        var builder = new PropertyResultBuilder(NetProperty.Reversibility);
        builder.SetStatus(DecideFromStateSpace(b, builder));
        return builder.Build();
    }

    private static TestResultStatus DecideFromStateSpace(AnalysisBundle b, PropertyResultBuilder r)
    {
        var ss = b.StateSpace;
        if (ss is null) { r.AddReason("State space analysis results are unavailable."); return TestResultStatus.Undecidable; }
        if (ss.HasErrors) { r.LogError(ss.ErrorMsg!); return TestResultStatus.Undecidable; }
        bool rev = ss.IsReversible();
        r.AddReason(rev
            ? "State space is strongly connected; initial marking is reachable from every reachable marking. Net is reversible."
            : "State space has more than one final SCC; initial marking is not universally reachable. Net is not reversible.");
        return rev ? TestResultStatus.Pass : TestResultStatus.Fail;
    }
}
