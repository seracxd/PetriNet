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
        var str = DecideFromStructure(b, builder);
        var cov = DecideFromCoverabilityTree(b, builder);
        var ss  = DecideFromStateSpace(b, builder);
        var inv = DecideFromInvariants(b, builder);
        var cls = DecideFromClassification(b, builder);
        builder.SetStatus(TestResultStatusExtensions.LogicalOr(str, cov, ss, inv, cls));
        return builder.Build();
    }

    /// <summary>
    /// Sufficient condition for ordinary nets: if the coverability tree
    /// contains a marking where every place is ω-marked, every transition is
    /// structurally enabled at that marking (ω ≥ w for every input weight).
    /// Karp-Miller's monotonicity then ensures we can reach this "saturation"
    /// marking from every reachable state, so every transition is L4-live.
    /// </summary>
    private static TestResultStatus DecideFromCoverabilityTree(AnalysisBundle b, PropertyResultBuilder r)
    {
        var cb = b.CoverabilityTree;
        if (cb is null || !b.IsOrdinaryNet) return TestResultStatus.Undecidable;
        bool allOmegaNode = cb.Nodes.Any(n => n.Marking.Length > 0 &&
            n.Marking.All(v => v == CoverabilityTreeBuilder.Omega));   // exact ω only — ordinary nets always accelerate to ω, never saturate
        if (allOmegaNode)
        {
            r.AddReason("Coverability tree reaches a marking with ω at every place — every transition is structurally enabled there, and Karp-Miller monotonicity makes that saturation marking reachable from every reachable state. Net is live.", TestResultStatus.Pass);
            return TestResultStatus.Pass;
        }
        return TestResultStatus.Undecidable;
    }

    /// <summary>
    /// Sufficient structural condition: if EVERY transition has no normal /
    /// inhibitor input arcs, every transition is enabled at every reachable
    /// marking — the net is L4-live by construction. Catches degenerate but
    /// valid cases like a single "source" transition that the FC theorem
    /// would otherwise wrongly reject.
    /// </summary>
    private static TestResultStatus DecideFromStructure(AnalysisBundle b, PropertyResultBuilder r)
    {
        if (b.Net.Transitions.Count == 0) return TestResultStatus.Undecidable;
        bool everyAlwaysEnabled = b.Net.Transitions.All(t =>
            !b.Net.Arcs.Any(a => a.TargetId == t.Id &&
                                 (a.ArcType == PnArcType.Normal || a.ArcType == PnArcType.Inhibitor)));
        if (everyAlwaysEnabled)
        {
            r.AddReason("Every transition has no constraining (normal/inhibitor) input arcs, so every transition is enabled at every reachable marking. Net is live.", TestResultStatus.Pass);
            return TestResultStatus.Pass;
        }
        return TestResultStatus.Undecidable;
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

        // T-invariant coverage is a NECESSARY condition for liveness only in
        // BOUNDED nets. An unbounded net can be L4-live via a source transition
        // (no inputs, always enabled) that no T-invariant covers — so applying
        // this check to unbounded nets produces false-Fail verdicts. Restrict
        // it to nets the cb tree confirms bounded.
        var tStatus = TestResultStatus.Undecidable;
        var cb = b.CoverabilityTree;
        bool boundedConfirmed = cb != null
            && !cb.IsTruncated
            && !cb.Nodes.Any(n => n.Marking.Any(CoverabilityTreeBuilder.GrowsWithoutBound));
        if (boundedConfirmed)
        {
            var coveredTrans = new HashSet<string>(inv.TInvariants.SelectMany(ti => ti.Structure.Keys));
            bool allCovered  = b.Net.Transitions.All(t => coveredTrans.Contains(t.Id));
            if (!allCovered)
            {
                tStatus = TestResultStatus.Fail;
                r.AddReason("Some transition is not covered by any T-invariant — a necessary condition for liveness fails (net is bounded).", TestResultStatus.Fail);
            }
        }

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
        // The marked-graph liveness theorem (every directed cycle marked ⟹ live)
        // is stated for ordinary marked graphs; restrict it to unit-weight nets so
        // weighted edges can't yield a misleading Pass.
        if (!cls.IsOfType(NetSubclass.MarkedGraph) || !cls.IsOfType(NetSubclass.Ordinary))
            return TestResultStatus.Undecidable;

        var cyc = b.Cycles;
        if (cyc is null) return TestResultStatus.Undecidable;

        bool eachHasTokens = cyc.Cycles.All(c => c.TokensInCycle > 0);
        if (eachHasTokens)
            r.AddReason("Net is an ordinary Marked graph and every cycle contains ≥ 1 token. Net is live.", TestResultStatus.Pass);
        return eachHasTokens ? TestResultStatus.Pass : TestResultStatus.Undecidable;
    }

    private static TestResultStatus DecidefreeChoice(AnalysisBundle b, PropertyResultBuilder r, ClassificationAnalysis cls)
    {
        if (!cls.IsOfType(NetSubclass.FreeChoice)) return TestResultStatus.Undecidable;

        // Commoner's theorem (FC liveness ⟺ every siphon has a marked trap)
        // is biconditional only for BOUNDED free-choice nets. Applying it to
        // an unbounded FC net produces false negatives — e.g. a "source"
        // transition that's structurally always-enabled would be reported
        // as not-live just because its output siphon has no marked trap.
        var cb = b.CoverabilityTree;
        if (cb is null) return TestResultStatus.Undecidable;
        bool grows = cb.Nodes.Any(n => n.Marking.Any(CoverabilityTreeBuilder.GrowsWithoutBound));
        if (grows || cb.IsTruncated)
        {
            r.AddReason("Commoner's theorem requires a bounded free-choice net; boundedness is not confirmed here.", TestResultStatus.Undecidable);
            return TestResultStatus.Undecidable;
        }

        var tc = b.TrapCotrap;
        if (tc is null || !tc.Cotraps.Any()) return TestResultStatus.Undecidable;

        var trapsWithTokens = tc.Traps.Where(t => t.HasToken).ToList();
        bool ok = tc.Cotraps.All(ct => trapsWithTokens.Any(trap => ct.PlaceIds.IsSupersetOf(trap.PlaceIds)));
        var status = ok ? TestResultStatus.Pass : TestResultStatus.Fail;
        r.AddReason(ok
            ? "Net is bounded Free-choice and each co-trap contains a trap with ≥ 1 token. Net is live."
            : "Net is bounded Free-choice but some co-trap contains no marked trap. Net is not live.",
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
            DecideFromCoverabilityTree(b, builder),
            DecideFromStateSpace(b, builder),
            DecideFromInvariants(b, builder),
            DecideFromClassification(b, builder)));
        return builder.Build();
    }

    /// <summary>
    /// Karp-Miller's coverability tree gives the canonical answer:
    ///   - any ω-node → unbounded (Fail);
    ///   - cb fully built, no ω → bounded (Pass);
    ///   - cb truncated → fall through, Undecidable here.
    /// </summary>
    private static TestResultStatus DecideFromCoverabilityTree(AnalysisBundle b, PropertyResultBuilder r)
    {
        var cb = b.CoverabilityTree;
        if (cb is null) return TestResultStatus.Undecidable;
        if (cb.HasErrors && !cb.IsTruncated) return TestResultStatus.Undecidable;

        // An ω-node (Karp-Miller) or a saturated node (ω-acceleration disabled for
        // inhibitor/reset nets) both witness a place that grows past any finite bound.
        // This proof is local to the witnessing node, so it stays valid even when the
        // tree is truncated — truncation never turns a definite "unbounded" into "unknown".
        bool grows = cb.Nodes.Any(n => n.Marking.Any(CoverabilityTreeBuilder.GrowsWithoutBound));
        if (grows)
        {
            r.AddReason("Coverability tree reaches a marking where a place grows without bound (ω, or the saturation ceiling for inhibitor/reset nets). Net is unbounded.", TestResultStatus.Fail);
            return TestResultStatus.Fail;
        }
        if (!cb.IsTruncated)
        {
            r.AddReason("Coverability tree is fully built with no unbounded markings — every reachable marking is finite. Net is bounded.", TestResultStatus.Pass);
            return TestResultStatus.Pass;
        }
        return TestResultStatus.Undecidable;
    }

    private static TestResultStatus DecideFromStateSpace(AnalysisBundle b, PropertyResultBuilder r)
    {
        var ss = b.StateSpace;
        if (ss is null) { r.AddReason("State space analysis results are unavailable.", TestResultStatus.Undecidable); return TestResultStatus.Undecidable; }
        if (ss.HasErrors) { r.LogError(ss.ErrorMsg!); return TestResultStatus.Undecidable; }

        // A marking that hit the saturation ceiling is a definite unboundedness
        // witness, independent of truncation: the place blew past every finite
        // bound during firing. This catches non-ordinary nets (ω-acceleration off)
        // whose state space terminates with a saturated-but-finite marking.
        bool saturated = ss.States.Any(s => s.Any(CoverabilityTreeBuilder.GrowsWithoutBound));
        if (saturated)
        {
            r.AddReason("A reachable marking reached the saturation ceiling — a place grows without bound. Net is unbounded.", TestResultStatus.Fail);
            return TestResultStatus.Fail;
        }

        // Truncation does NOT prove unboundedness — the net might just have more
        // reachable markings than the cap. The cb tree branch above is the only
        // place that can definitively say "unbounded".
        if (ss.IsTruncated)
        {
            r.AddReason($"State space exceeded the {b.StateSpace?.States.Count} marking cap before full exploration — bounded/unbounded verdict unavailable from the state space alone.", TestResultStatus.Undecidable);
            return TestResultStatus.Undecidable;
        }
        r.AddReason("Token counts are finite at every reachable marking. Net is bounded.", TestResultStatus.Pass);
        return TestResultStatus.Pass;
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
        // "State machine ⟹ bounded" relies on token conservation, which only holds
        // when every arc has unit weight (Ordinary). A structural state machine with
        // a heavy-weight output arc (consume 1, produce many) is NOT token-conserving
        // and can grow without bound, so the Ordinary guard is mandatory here.
        bool sm = cls.IsOfType(NetSubclass.StateMachine) && cls.IsOfType(NetSubclass.Ordinary);
        if (sm)
            r.AddReason("Net is an ordinary State machine (unit weights), so token count is conserved. Net is bounded.", TestResultStatus.Pass);
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
            DecideFromCoverabilityTree(b, builder),
            DecideFromStateSpace(b, builder),
            DecideFromClassification(b, builder)));
        return builder.Build();
    }

    /// <summary>
    /// The cb tree dominates every reachable marking:
    ///   - any cb node value > 1 (including ω) → some reachable marking has > 1 → not safe (Fail);
    ///   - cb fully built and every cb value ≤ 1 → every reachable marking has ≤ 1 → safe (Pass);
    ///   - cb truncated → unknown.
    /// </summary>
    private static TestResultStatus DecideFromCoverabilityTree(AnalysisBundle b, PropertyResultBuilder r)
    {
        var cb = b.CoverabilityTree;
        if (cb is null) return TestResultStatus.Undecidable;
        if (cb.HasErrors && !cb.IsTruncated) return TestResultStatus.Undecidable;

        bool anyOver1 = cb.Nodes.Any(n => n.Marking.Any(v =>
            v == CoverabilityTreeBuilder.Omega || v > 1));
        if (anyOver1)
        {
            r.AddReason("Coverability tree contains a reachable marking with > 1 tokens (or ω) in some place. Net is not safe.", TestResultStatus.Fail);
            return TestResultStatus.Fail;
        }
        if (!cb.IsTruncated)
        {
            r.AddReason("Coverability tree is fully built and every place stays ≤ 1 token. Net is safe.", TestResultStatus.Pass);
            return TestResultStatus.Pass;
        }
        return TestResultStatus.Undecidable;
    }

    private static TestResultStatus DecideFromStateSpace(AnalysisBundle b, PropertyResultBuilder r)
    {
        var ss = b.StateSpace;
        if (ss is null) { r.AddReason("State space analysis results are unavailable.", TestResultStatus.Undecidable); return TestResultStatus.Undecidable; }
        if (ss.HasErrors) { r.LogError(ss.ErrorMsg!); return TestResultStatus.Undecidable; }
        // Truncated state space: even if every explored marking has ≤ 1, an
        // unexplored one could have > 1. Don't claim Pass or Fail.
        if (ss.IsTruncated) return TestResultStatus.Undecidable;
        bool safe = ss.States.All(s => s.All(t => t <= 1));
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
        // Token conservation (and hence the token-sum argument below) holds only for
        // ordinary, unit-weight state machines. A heavy-weight SM can blow a single
        // token up into many, so we must not give a safety verdict from structure alone.
        if (!cls.IsOfType(NetSubclass.StateMachine) || !cls.IsOfType(NetSubclass.Ordinary))
            return TestResultStatus.Undecidable;
        int sum = b.Net.Places.Sum(p => p.Tokens);
        var status = sum <= 1 ? TestResultStatus.Pass : TestResultStatus.Fail;
        r.AddReason(sum <= 1 ? "Net is an ordinary State machine with initial token sum ≤ 1. Net is safe."
                             : "Net is an ordinary State machine but initial token sum > 1. Net is unsafe.",
                    status);
        return status;
    }

    private static TestResultStatus DecideMarkedGraph(AnalysisBundle b, PropertyResultBuilder r, ClassificationAnalysis cls)
    {
        // The marked-graph "≤ 1 token per cycle ⟹ safe" theorem counts token flow
        // and so relies on unit weights. A weight-2 production arc can deposit 2
        // tokens in a place that sits in a 1-token cycle, breaking safety. Require
        // an ordinary (unit-weight) net before trusting the structural verdict.
        if (!cls.IsOfType(NetSubclass.MarkedGraph) || !cls.IsOfType(NetSubclass.Ordinary))
            return TestResultStatus.Undecidable;
        var cyc = b.Cycles;
        if (cyc is null) return TestResultStatus.Undecidable;
        var cyclePlaces = new HashSet<string>(cyc.Cycles.SelectMany(c => c.PlaceIds));
        bool allCovered = b.Net.Places.All(p => cyclePlaces.Contains(p.Id));
        bool noOverload = cyc.Cycles.All(c => c.TokensInCycle <= 1);
        if (allCovered && noOverload)
            r.AddReason("Net is an ordinary Marked graph; every place is in a cycle with ≤ 1 token. Net is safe.", TestResultStatus.Pass);
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
        if (conservative)
        {
            r.AddReason("Every place is covered by a P-invariant, so a positive weighting Yᵀ·M is preserved across all reachable markings. Net is conservative (in the weighted sense). Note: the raw token count need not be constant — see Strict conservativeness for that.", TestResultStatus.Pass);
            return TestResultStatus.Pass;
        }
        // Missing coverage might just mean the Farkas elimination ran past its
        // candidate cap and dropped some P-invariants — we can't claim "not
        // conservative" without seeing the full minimal-invariant set.
        if (inv.WasTruncated)
        {
            r.AddReason("P-invariant search truncated past the candidate cap — coverage check is inconclusive.", TestResultStatus.Undecidable);
            return TestResultStatus.Undecidable;
        }
        r.AddReason("Some place is not covered by any P-invariant. Net is not conservative.", TestResultStatus.Fail);
        return TestResultStatus.Fail;
    }

    /// <summary>
    /// True iff the net is strictly conservative: the total token count is constant
    /// across all reachable markings, i.e. the all-ones vector is a P-invariant
    /// (1ᵀ·C = 0 — every transition consumes exactly as many tokens as it produces).
    /// </summary>
    internal static bool HasAllOnesPInvariant(AnalysisBundle b)
    {
        // Direct structural check on the incidence matrix: column sums all zero.
        // This is independent of the (possibly capped) Farkas enumeration, so it
        // stays correct even when the minimal-invariant search was truncated.
        var net = b.Net;
        foreach (var t in net.Transitions)
        {
            int delta = net.OutputArcs(t.Id).Where(a => a.ArcType == PnArcType.Normal).Sum(a => a.Weight)
                      - net.InputArcs(t.Id).Where(a => a.ArcType == PnArcType.Normal).Sum(a => a.Weight);
            if (delta != 0) return false;
        }
        return true;
    }

    private static TestResultStatus DecideFromClassification(AnalysisBundle b, PropertyResultBuilder r)
    {
        var cls = b.Classification;
        if (cls is null || cls.HasErrors) return TestResultStatus.Undecidable;
        // Conservativeness (token count is invariant) requires unit weights — a
        // heavy-weight state machine changes the token total on every firing.
        bool sm = cls.IsOfType(NetSubclass.StateMachine) && cls.IsOfType(NetSubclass.Ordinary);
        if (sm)
            r.AddReason("Net is an ordinary State machine (unit weights), so the token count is invariant. Net is conservative.", TestResultStatus.Pass);
        return sm ? TestResultStatus.Pass : TestResultStatus.Undecidable;
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Strict conservativeness (token COUNT preserved — all-ones P-invariant)
// ─────────────────────────────────────────────────────────────────────────────

public sealed class StrictConservativenessTest
{
    public PropertyTestResult Run(AnalysisBundle b)
    {
        var builder = new PropertyResultBuilder(NetProperty.StrictConservativeness);
        builder.SetStatus(Decide(b, builder));
        return builder.Build();
    }

    private static TestResultStatus Decide(AnalysisBundle b, PropertyResultBuilder r)
    {
        if (b.Net.Transitions.Count == 0)
        {
            r.AddReason("Net has no transitions; the token count trivially never changes.", TestResultStatus.Pass);
            return TestResultStatus.Pass;
        }

        // Reset arcs remove an unbounded number of tokens on firing, so the
        // incidence matrix does not capture the true token-count change — we can't
        // give a definite strict-conservativeness verdict for those nets.
        if (b.HasResetArcs)
        {
            r.AddReason("Net contains reset arcs, which discard a variable number of tokens. The token-count change is not captured by the incidence matrix, so strict conservativeness cannot be decided structurally.", TestResultStatus.Undecidable);
            return TestResultStatus.Undecidable;
        }

        if (ConservativenessTest.HasAllOnesPInvariant(b))
        {
            r.AddReason("Every transition consumes exactly as many tokens as it produces (the all-ones vector is a P-invariant, 1ᵀ·C = 0), so the total token count is constant across all reachable markings. Net is strictly conservative.", TestResultStatus.Pass);
            return TestResultStatus.Pass;
        }

        var offenders = b.Net.Transitions
            .Where(t =>
                b.Net.OutputArcs(t.Id).Where(a => a.ArcType == PnArcType.Normal).Sum(a => a.Weight) !=
                b.Net.InputArcs(t.Id).Where(a => a.ArcType == PnArcType.Normal).Sum(a => a.Weight))
            .Select(t => t.Name)
            .ToList();
        r.AddReason($"Transition(s) {string.Join(", ", offenders)} change the total token count (consume ≠ produce), so the count is not preserved. Net is not strictly conservative.", TestResultStatus.Fail);
        return TestResultStatus.Fail;
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
        if (repetitive)
        {
            r.AddReason("All transitions are covered by T-invariants. Net is repetitive.", TestResultStatus.Pass);
            return TestResultStatus.Pass;
        }
        if (inv.WasTruncated)
        {
            r.AddReason("T-invariant search truncated past the candidate cap — coverage check is inconclusive.", TestResultStatus.Undecidable);
            return TestResultStatus.Undecidable;
        }
        r.AddReason("Some transition is not covered by any T-invariant. Net is not repetitive.", TestResultStatus.Fail);
        return TestResultStatus.Fail;
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
            DecideFromStructure(b, builder),
            DecideFromStateSpace(b, builder),
            DecideFromCoverabilityTree(b, builder),
            DecideFromLiveness(b, builder)));
        return builder.Build();
    }

    /// <summary>
    /// Sufficient structural condition: if any transition has no constraining
    /// input arcs (no normal / inhibitor inputs), that transition is enabled
    /// at every reachable marking. So no reachable marking can be a deadlock.
    /// </summary>
    private static TestResultStatus DecideFromStructure(AnalysisBundle b, PropertyResultBuilder r)
    {
        var alwaysEnabled = b.Net.Transitions
            .Where(t => !b.Net.Arcs.Any(a => a.TargetId == t.Id &&
                                             (a.ArcType == PnArcType.Normal || a.ArcType == PnArcType.Inhibitor)))
            .ToList();
        if (alwaysEnabled.Count > 0)
        {
            var names = string.Join(", ", alwaysEnabled.Select(t => t.Name));
            r.AddReason($"Transition(s) {names} have no constraining input arcs and are enabled at every reachable marking. Net is deadlock-free.", TestResultStatus.Pass);
            return TestResultStatus.Pass;
        }
        return TestResultStatus.Undecidable;
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

    /// <summary>
    /// Use the coverability tree when the (truncated) state space couldn't decide.
    /// Karp-Miller guarantees every cb node corresponds to a reachable marking, so:
    ///   - cb has an IsDeadlock node → that marking is a true reachable deadlock → Fail.
    /// We deliberately do NOT return Pass here even when no cb node is marked
    /// deadlock — covering doesn't preserve enabledness, so absence of cb deadlocks
    /// is necessary but not sufficient for deadlock-freedom in unbounded nets.
    /// </summary>
    private static TestResultStatus DecideFromCoverabilityTree(AnalysisBundle b, PropertyResultBuilder r)
    {
        var cb = b.CoverabilityTree;
        if (cb is null || cb.HasErrors && !cb.IsTruncated) return TestResultStatus.Undecidable;

        bool hasCbDeadlock = cb.Nodes.Any(n => n.IsDeadlock && !n.IsDuplicate);
        if (hasCbDeadlock)
        {
            r.AddReason("Coverability tree contains a reachable marking with no enabled transitions. Net is not deadlock-free.", TestResultStatus.Fail);
            return TestResultStatus.Fail;
        }
        return TestResultStatus.Undecidable;
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
        builder.SetStatus(TestResultStatusExtensions.LogicalOr(
            DecideFromStructure(b, builder),
            DecideFromInvariants(b, builder),
            DecideFromStateSpace(b, builder)));
        return builder.Build();
    }

    /// <summary>
    /// Necessary condition for reversibility: every transition must appear in
    /// at least one T-invariant. A T-invariant represents a firing sequence
    /// that returns to the same marking — if a transition isn't in any
    /// T-invariant, its effect can't be "undone" by any cyclic sequence, so
    /// the net can't return to M₀ after firing it. This works for unbounded
    /// nets too (T-invariants are structural, defined on the incidence
    /// matrix alone).
    /// </summary>
    private static TestResultStatus DecideFromInvariants(AnalysisBundle b, PropertyResultBuilder r)
    {
        var inv = b.Invariants;
        if (inv is null || inv.WasSkipped || inv.HasErrors) return TestResultStatus.Undecidable;
        if (!b.IsOrdinaryNet) return TestResultStatus.Undecidable;

        var covered = new HashSet<string>(inv.TInvariants.SelectMany(ti => ti.Structure.Keys));
        var uncovered = b.Net.Transitions.Where(t => !covered.Contains(t.Id)).Select(t => t.Name).ToList();
        if (uncovered.Count > 0)
        {
            // If invariant search hit the candidate cap, the uncovered transition
            // might be in a T-invariant we dropped — can't claim Fail.
            if (inv.WasTruncated) return TestResultStatus.Undecidable;
            r.AddReason($"Transition(s) {string.Join(", ", uncovered)} are not covered by any T-invariant — their effect can't be undone by any cyclic firing sequence. Net is not reversible.", TestResultStatus.Fail);
            return TestResultStatus.Fail;
        }
        return TestResultStatus.Undecidable;
    }

    /// <summary>
    /// Sufficient structural condition for NOT reversible: if any place can
    /// receive tokens (some T→P arc exists) but no transition can consume
    /// from it (no P→T normal or reset arc), and some reachable marking has
    /// strictly more tokens at that place than the initial count, then the
    /// initial marking is unrecoverable — token counts at that place can only
    /// grow. This is the exact pattern of a "producer-only" place / source
    /// transition, which the state-space SCC analysis can't detect when the
    /// state space is truncated.
    /// </summary>
    private static TestResultStatus DecideFromStructure(AnalysisBundle b, PropertyResultBuilder r)
    {
        var cb = b.CoverabilityTree;
        if (cb is null) return TestResultStatus.Undecidable;

        var places = b.Net.Places;
        for (int i = 0; i < places.Count; i++)
        {
            var place = places[i];
            bool hasConsumer = b.Net.Arcs.Any(a =>
                a.SourceId == place.Id &&
                (a.ArcType == PnArcType.Normal || a.ArcType == PnArcType.Reset));
            if (hasConsumer) continue;

            // No transition consumes from this place. Look for any reachable
            // marking where its token count exceeds the initial value — ω
            // counts (which the Karp-Miller acceleration uses for unbounded
            // growth) trivially satisfy this.
            bool grew = cb.Nodes.Any(n =>
                i < n.Marking.Length &&
                (n.Marking[i] == CoverabilityTreeBuilder.Omega || n.Marking[i] > place.Tokens));

            if (grew)
            {
                r.AddReason($"Place {place.Name} reaches marking values above its initial count of {place.Tokens}, but no transition can consume from it. The initial token count is unrecoverable. Net is not reversible.", TestResultStatus.Fail);
                return TestResultStatus.Fail;
            }
        }
        return TestResultStatus.Undecidable;
    }

    private static TestResultStatus DecideFromStateSpace(AnalysisBundle b, PropertyResultBuilder r)
    {
        var ss = b.StateSpace;
        if (ss is null) { r.AddReason("State space analysis results are unavailable.", TestResultStatus.Undecidable); return TestResultStatus.Undecidable; }
        if (ss.HasErrors) { r.LogError(ss.ErrorMsg!); return TestResultStatus.Undecidable; }
        // Reversibility is a global property of the FULL reachability graph.
        // A truncated state space can't disprove it (the missing portion might
        // include all the return-paths to M₀), so we must not return Fail.
        if (ss.IsTruncated)
        {
            r.AddReason("State space is truncated — reversibility verdict requires the full reachability graph.", TestResultStatus.Undecidable);
            return TestResultStatus.Undecidable;
        }
        bool rev = ss.IsReversible();
        var status = rev ? TestResultStatus.Pass : TestResultStatus.Fail;
        r.AddReason(rev
            ? "State space is strongly connected; initial marking is reachable from every reachable marking. Net is reversible."
            : "State space has more than one final SCC; initial marking is not universally reachable. Net is not reversible.",
            status);
        return status;
    }
}
