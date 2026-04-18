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
        if (ss is null) { r.AddReason("State space analysis results are unavailable.", TestResultStatus.Undecidable); return TestResultStatus.Undecidable; }
        if (ss.HasErrors) { r.LogError(ss.ErrorMsg!); return TestResultStatus.Undecidable; }

        if (ss.IsTruncated)
        {
            // Full liveness verdict is unavailable. Report partial L1-liveness:
            // any transition that fired at least once in the partial space is confirmed
            // L1-live. Transitions that didn't fire may still fire in unexplored states.
            var fired   = ss.FiredTransitions();
            var notFired = b.Net.Transitions.Where(t => !fired.Contains(t.Id)).Select(t => t.Name).ToList();
            if (fired.Count > 0)
                r.AddReason(
                    $"State space is truncated. {fired.Count} transition(s) fired at least once (confirmed L1-live). " +
                    (notFired.Count > 0
                        ? $"The following were not observed firing and may or may not be live: {string.Join(", ", notFired)}."
                        : "All transitions were observed firing at least once."),
                    TestResultStatus.Undecidable);
            else
                r.AddReason("State space is truncated and no transitions fired — full liveness verdict is unavailable.", TestResultStatus.Undecidable);
            return TestResultStatus.Undecidable;
        }

        bool live = ss.IsLive(b.Net.Transitions.Count);
        var status = live ? TestResultStatus.Pass : TestResultStatus.Fail;
        r.AddReason(live
            ? "Each final SCC of the state space contains all transitions as arc labels. Net is live."
            : "Not all final SCCs contain every transition as arc labels. Net is not live.",
            status);
        return status;
    }

    private static TestResultStatus DecideFromInvariants(AnalysisBundle b, PropertyResultBuilder r)
    {
        var inv = b.Invariants;
        if (inv is null) { r.AddReason("Invariant analysis results are unavailable.", TestResultStatus.Undecidable); return TestResultStatus.Undecidable; }
        if (inv.WasSkipped) { r.AddReason(inv.ErrorMsg!, TestResultStatus.Undecidable); return TestResultStatus.Undecidable; }
        if (inv.HasErrors) { r.LogError(inv.ErrorMsg!); return TestResultStatus.Undecidable; }

        if (!b.IsOrdinaryNet)
        {
            r.AddReason("Net contains inhibitor or reset arcs. Invariant-based liveness checks use the ordinary incidence matrix and are not reliable for this net.", TestResultStatus.Undecidable);
            return TestResultStatus.Undecidable;
        }

        var pStatus = TestResultStatus.Undecidable;
        foreach (var pinv in inv.PInvariants)
        {
            int system = pinv.Structure.Sum(kv =>
                kv.Value * (b.Net.PlaceById.TryGetValue(kv.Key, out var pl) ? pl.Tokens : 0));
            if (system <= 0 && pinv.Structure.Keys.Any(pid => b.Net.ConnectedTransitions(pid).Any()))
            {
                pStatus = TestResultStatus.Fail;
                r.AddReason("A P-invariant with system value ≤ 0 exists whose support contains a place connected to transitions. Net is not live.", TestResultStatus.Fail);
                break;
            }
        }

        var coveredTrans = new HashSet<string>(inv.TInvariants.SelectMany(ti => ti.Structure.Keys));
        bool allCovered  = b.Net.Transitions.All(t => coveredTrans.Contains(t.Id));
        var tStatus = allCovered ? TestResultStatus.Undecidable : TestResultStatus.Fail;
        if (!allCovered)
            r.AddReason("Some transition is not covered by any T-invariant — a necessary condition for liveness fails.", TestResultStatus.Fail);

        return TestResultStatusExtensions.LogicalOr(pStatus, tStatus);
    }

    private static TestResultStatus DecideFromClassification(AnalysisBundle b, PropertyResultBuilder r)
    {
        var cls = b.Classification;
        if (cls is null) { r.AddReason("Classification results are unavailable.", TestResultStatus.Undecidable); return TestResultStatus.Undecidable; }
        if (cls.HasErrors) { r.LogError(cls.ErrorMsg!); return TestResultStatus.Undecidable; }

        if (!b.IsOrdinaryNet)
        {
            r.AddReason("Net contains inhibitor or reset arcs. Structural liveness theorems (Marked graph, Free-choice) require ordinary nets and are skipped.", TestResultStatus.Undecidable);
            // StateMachine check is still valid: SM classification already requires all-normal arcs.
            return DecideStateMachine(b, r, cls);
        }

        return TestResultStatusExtensions.LogicalOr(
            DecideStateMachine(b, r, cls),
            DecideMarkedGraph(b, r, cls),
            DecidefreeChoice(b, r, cls));
    }

    private static TestResultStatus DecideStateMachine(AnalysisBundle b, PropertyResultBuilder r, ClassificationAnalysis cls)
    {
        if (!cls.IsOfType(NetSubclass.StateMachine)) return TestResultStatus.Undecidable;

        int tokenSum = b.Net.Places.Sum(p => p.Tokens);
        if (tokenSum <= 0) return TestResultStatus.Undecidable;

        bool sc = IsNetStronglyConnected(b.Net);
        if (sc)
            r.AddReason("Net is a strongly connected State machine with tokens > 0. Net is live.", TestResultStatus.Pass);
        return sc ? TestResultStatus.Pass : TestResultStatus.Undecidable;
    }

    private static TestResultStatus DecideMarkedGraph(AnalysisBundle b, PropertyResultBuilder r, ClassificationAnalysis cls)
    {
        if (!cls.IsOfType(NetSubclass.MarkedGraph)) return TestResultStatus.Undecidable;

        var cyc = b.Cycles;
        if (cyc is null) return TestResultStatus.Undecidable;

        bool eachHasTokens = cyc.Cycles.All(c => c.TokensInCycle > 0);
        if (eachHasTokens)
            r.AddReason("Net is a Marked graph and every cycle contains ≥ 1 token. Net is live.", TestResultStatus.Pass);
        return eachHasTokens ? TestResultStatus.Pass : TestResultStatus.Undecidable;
    }

    private static TestResultStatus DecidefreeChoice(AnalysisBundle b, PropertyResultBuilder r, ClassificationAnalysis cls)
    {
        if (!cls.IsOfType(NetSubclass.FreeChoice)) return TestResultStatus.Undecidable;

        var tc = b.TrapCotrap;
        if (tc is null || !tc.Cotraps.Any()) return TestResultStatus.Undecidable;

        var trapsWithTokens = tc.Traps.Where(t => t.HasToken).ToList();
        bool ok = tc.Cotraps.All(ct => trapsWithTokens.Any(trap => ct.PlaceIds.IsSupersetOf(trap.PlaceIds)));
        var status = ok ? TestResultStatus.Pass : TestResultStatus.Fail;
        r.AddReason(ok
            ? "Net is Free-choice and each co-trap contains a trap with ≥ 1 token. Net is live."
            : "Net is Free-choice but some co-trap contains no marked trap. Net is not live.",
            status);
        return status;
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
        if (ss is null) { r.AddReason("State space analysis results are unavailable.", TestResultStatus.Undecidable); return TestResultStatus.Undecidable; }
        if (ss.HasErrors) { r.LogError(ss.ErrorMsg!); return TestResultStatus.Undecidable; }
        bool bounded = ss.IsBounded();
        var status = bounded ? TestResultStatus.Pass : TestResultStatus.Fail;
        r.AddReason(bounded
            ? "Token counts are finite at every reachable marking. Net is bounded."
            : "Token count in some place may grow to infinity. Net is unbounded.",
            status);
        return status;
    }

    private static TestResultStatus DecideFromInvariants(AnalysisBundle b, PropertyResultBuilder r)
    {
        var inv = b.Invariants;
        if (inv is null) { r.AddReason("Invariant analysis results are unavailable.", TestResultStatus.Undecidable); return TestResultStatus.Undecidable; }
        if (inv.WasSkipped) { r.AddReason(inv.ErrorMsg!, TestResultStatus.Undecidable); return TestResultStatus.Undecidable; }
        if (inv.HasErrors) { r.LogError(inv.ErrorMsg!); return TestResultStatus.Undecidable; }

        if (!b.IsOrdinaryNet)
        {
            r.AddReason("Net contains inhibitor or reset arcs. P-invariant coverage does not guarantee boundedness for non-ordinary nets.", TestResultStatus.Undecidable);
            return TestResultStatus.Undecidable;
        }

        var covered = new HashSet<string>(inv.PInvariants.SelectMany(pi => pi.Structure.Keys));
        bool allCovered = b.Net.Places.All(p => covered.Contains(p.Id));
        if (allCovered)
            r.AddReason("All places are covered by P-invariants. Assuming finite initial marking, net is bounded.", TestResultStatus.Pass);
        return allCovered ? TestResultStatus.Pass : TestResultStatus.Undecidable;
    }

    private static TestResultStatus DecideFromClassification(AnalysisBundle b, PropertyResultBuilder r)
    {
        var cls = b.Classification;
        if (cls is null || cls.HasErrors) return TestResultStatus.Undecidable;
        bool sm = cls.IsOfType(NetSubclass.StateMachine);
        if (sm)
            r.AddReason("Net is a State machine, therefore it is bounded.", TestResultStatus.Pass);
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
        if (ss is null) { r.AddReason("State space analysis results are unavailable.", TestResultStatus.Undecidable); return TestResultStatus.Undecidable; }
        if (ss.HasErrors) { r.LogError(ss.ErrorMsg!); return TestResultStatus.Undecidable; }
        bool safe = ss.IsSafe();
        var status = safe ? TestResultStatus.Pass : TestResultStatus.Fail;
        r.AddReason(safe ? "All places have ≤ 1 token at every reachable marking. Net is safe."
                         : "Some place has > 1 token at some reachable marking. Net is unsafe.",
                    status);
        return status;
    }

    private static TestResultStatus DecideFromClassification(AnalysisBundle b, PropertyResultBuilder r)
    {
        var cls = b.Classification;
        if (cls is null || cls.HasErrors) return TestResultStatus.Undecidable;

        if (!b.IsOrdinaryNet)
        {
            r.AddReason("Net contains inhibitor or reset arcs. Structural safety theorem (Marked graph) requires ordinary nets and is skipped.", TestResultStatus.Undecidable);
            // StateMachine safety check is purely token-count based — still valid.
            return DecideStateMachine(b, r, cls);
        }

        return TestResultStatusExtensions.LogicalOr(
            DecideStateMachine(b, r, cls),
            DecideMarkedGraph(b, r, cls));
    }

    private static TestResultStatus DecideStateMachine(AnalysisBundle b, PropertyResultBuilder r, ClassificationAnalysis cls)
    {
        if (!cls.IsOfType(NetSubclass.StateMachine)) return TestResultStatus.Undecidable;
        int sum = b.Net.Places.Sum(p => p.Tokens);
        var status = sum <= 1 ? TestResultStatus.Pass : TestResultStatus.Fail;
        r.AddReason(sum <= 1 ? "Net is a State machine with initial token sum ≤ 1. Net is safe."
                             : "Net is a State machine but initial token sum > 1. Net is unsafe.",
                    status);
        return status;
    }

    private static TestResultStatus DecideMarkedGraph(AnalysisBundle b, PropertyResultBuilder r, ClassificationAnalysis cls)
    {
        if (!cls.IsOfType(NetSubclass.MarkedGraph)) return TestResultStatus.Undecidable;
        var cyc = b.Cycles;
        if (cyc is null) return TestResultStatus.Undecidable;
        var cyclePlaces = new HashSet<string>(cyc.Cycles.SelectMany(c => c.PlaceIds));
        bool allCovered = b.Net.Places.All(p => cyclePlaces.Contains(p.Id));
        bool noOverload = cyc.Cycles.All(c => c.TokensInCycle <= 1);
        if (allCovered && noOverload)
            r.AddReason("Net is a Marked graph; every place is in a cycle with ≤ 1 token. Net is safe.", TestResultStatus.Pass);
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
        if (inv is null) { r.AddReason("Invariant analysis results are unavailable.", TestResultStatus.Undecidable); return TestResultStatus.Undecidable; }
        if (inv.WasSkipped) { r.AddReason(inv.ErrorMsg!, TestResultStatus.Undecidable); return TestResultStatus.Undecidable; }
        if (inv.HasErrors) { r.LogError(inv.ErrorMsg!); return TestResultStatus.Undecidable; }

        if (!b.IsOrdinaryNet)
        {
            r.AddReason("Net contains inhibitor or reset arcs. Conservativeness is defined via P-invariants of the ordinary incidence matrix, which does not capture non-ordinary arc effects.", TestResultStatus.Undecidable);
            return TestResultStatus.Undecidable;
        }

        var covered = new HashSet<string>(inv.PInvariants.SelectMany(pi => pi.Structure.Keys));
        bool conservative = b.Net.Places.All(p => covered.Contains(p.Id));
        var status = conservative ? TestResultStatus.Pass : TestResultStatus.Fail;
        r.AddReason(conservative
            ? "All places are covered by P-invariants. Net is conservative."
            : "Some place is not covered by any P-invariant. Net is not conservative.",
            status);
        return status;
    }

    private static TestResultStatus DecideFromClassification(AnalysisBundle b, PropertyResultBuilder r)
    {
        var cls = b.Classification;
        if (cls is null || cls.HasErrors) return TestResultStatus.Undecidable;
        bool sm = cls.IsOfType(NetSubclass.StateMachine);
        if (sm)
            r.AddReason("Net is a State machine, therefore it is conservative.", TestResultStatus.Pass);
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
        if (inv is null) { r.AddReason("Invariant analysis results are unavailable.", TestResultStatus.Undecidable); return TestResultStatus.Undecidable; }
        if (inv.WasSkipped) { r.AddReason(inv.ErrorMsg!, TestResultStatus.Undecidable); return TestResultStatus.Undecidable; }
        if (inv.HasErrors) { r.LogError(inv.ErrorMsg!); return TestResultStatus.Undecidable; }

        if (!b.IsOrdinaryNet)
        {
            r.AddReason("Net contains inhibitor or reset arcs. Repetitiveness is defined via T-invariants of the ordinary incidence matrix, which does not capture non-ordinary arc effects.", TestResultStatus.Undecidable);
            return TestResultStatus.Undecidable;
        }

        var covered = new HashSet<string>(inv.TInvariants.SelectMany(ti => ti.Structure.Keys));
        bool repetitive = b.Net.Transitions.All(t => covered.Contains(t.Id));
        var status = repetitive ? TestResultStatus.Pass : TestResultStatus.Fail;
        r.AddReason(repetitive
            ? "All transitions are covered by T-invariants. Net is repetitive."
            : "Some transition is not covered by any T-invariant. Net is not repetitive.",
            status);
        return status;
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
        if (ss is null) { r.AddReason("State space analysis results are unavailable.", TestResultStatus.Undecidable); return TestResultStatus.Undecidable; }
        if (ss.HasErrors) { r.LogError(ss.ErrorMsg!); return TestResultStatus.Undecidable; }

        if (ss.IsTruncated)
        {
            // Full verdict is unavailable, but we can still report definite deadlocks:
            // a state with no outgoing edges AND no enabled transitions is a true deadlock
            // regardless of how many other states were cut off.
            if (ss.HasDefiniteDeadlock())
            {
                r.AddReason("State space is truncated, but at least one reachable state has no enabled transitions. Net is not deadlock-free.", TestResultStatus.Fail);
                return TestResultStatus.Fail;
            }
            return TestResultStatus.Undecidable;
        }

        bool df = ss.IsDeadlockFree();
        var status = df ? TestResultStatus.Pass : TestResultStatus.Fail;
        r.AddReason(df ? "Every state space node has ≥ 1 successor. Net is deadlock-free."
                       : "Some state space node has no successors. Net is not deadlock-free.",
                    status);
        return status;
    }

    private static TestResultStatus DecideFromLiveness(AnalysisBundle b, PropertyResultBuilder r)
    {
        if (!b.PropertyResults.TryGetValue(NetProperty.Liveness, out var liveness)) return TestResultStatus.Undecidable;
        if (liveness.Status != TestResultStatus.Pass) return TestResultStatus.Undecidable;
        r.AddReason("Net is live, therefore it is deadlock-free.", TestResultStatus.Pass);
        return TestResultStatus.Pass;
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
        if (ss is null) { r.AddReason("State space analysis results are unavailable.", TestResultStatus.Undecidable); return TestResultStatus.Undecidable; }
        if (ss.HasErrors) { r.LogError(ss.ErrorMsg!); return TestResultStatus.Undecidable; }
        bool rev = ss.IsReversible();
        var status = rev ? TestResultStatus.Pass : TestResultStatus.Fail;
        r.AddReason(rev
            ? "State space is strongly connected; initial marking is reachable from every reachable marking. Net is reversible."
            : "State space has more than one final SCC; initial marking is not universally reachable. Net is not reversible.",
            status);
        return status;
    }
}
