using Analysis;
using Analysis.Algorithms;
using PetriEditor.Tests.Helpers;

namespace PetriEditor.Tests;

/// <summary>
/// Regression coverage for the orchestrator-level control flow on unbounded
/// nets: cb tree with ω, skipped invariants, truncated state space. The
/// existing PropertyTestsTests build bundles by hand which bypasses this
/// path entirely — these tests assert what the user sees end-to-end.
/// </summary>
public class UnboundedNetVerdictTests
{
    /// <summary>
    /// Simple producer: T1 consumes 1 from P1 and writes 2 back, net +1 each
    /// firing. Karp-Miller introduces ω at P1 → net is unbounded.
    /// </summary>
    private static PetriNetSnapshot ProducerNet() => new NetBuilder()
        .Place("P1", tokens: 1)
        .Transition("T1")
        .Arc("P1", "T1").Arc("T1", "P1").Arc("T1", "P1")
        .Build();

    [Fact]
    public void Bundle_PicksUpUnboundedFromCoverabilityTree()
    {
        var bundle = OrchestratorBundleBuilder.Build(ProducerNet());

        Assert.True(bundle.IsUnbounded);
        Assert.Contains(bundle.CoverabilityTree!.Nodes,
            n => n.Marking.Any(v => v == CoverabilityTreeBuilder.Omega));
        // Invariants are now computed even for unbounded ordinary nets —
        // T-invariants are structural and let ReversibilityTest decide many
        // unbounded cases that were previously Inconclusive.
        Assert.False(bundle.Invariants!.WasSkipped);
        Assert.True(bundle.StateSpace!.IsTruncated);
    }

    [Fact]
    public void Boundedness_DefinitivelyUnbounded_IsFail()
    {
        var bundle = OrchestratorBundleBuilder.Build(ProducerNet());
        var result = new BoundednessTest().Run(bundle);

        Assert.Equal(TestResultStatus.Fail, result.Status);
    }

    [Fact]
    public void Safety_DefinitivelyUnbounded_IsFail()
    {
        var bundle = OrchestratorBundleBuilder.Build(ProducerNet());
        var result = new SafetyTest().Run(bundle);

        Assert.Equal(TestResultStatus.Fail, result.Status);
    }

    [Fact]
    public void DeadlockFree_CoverabilityHasDeadlockNode_IsFail()
    {
        // A net where firing T1 empties P1 with no further enabled transitions.
        // After firing, marking [0, 1] has no enabled successor → cb tree
        // marks the corresponding node as IsDeadlock. DeadlockFreeTest must
        // detect this via the cb tree even when the state space is truncated.
        var net = new NetBuilder()
            .Place("P1", tokens: 1).Place("P2")
            .Transition("T1")
            .Arc("P1", "T1").Arc("T1", "P2")
            .Build();

        var bundle = OrchestratorBundleBuilder.Build(net);
        var result = new DeadlockFreeTest().Run(bundle);

        Assert.Equal(TestResultStatus.Fail, result.Status);
    }

    [Fact]
    public void DeadlockFree_TruncatedStateSpace_UsesCoverabilityTree()
    {
        // Unbounded net with NO reachable deadlock — every marking has T1
        // fireable. Truncated state space alone yields Undecidable; we add a
        // cb-tree fallback that returns the same conservative answer here
        // (covering doesn't preserve enabledness, so we can't claim Pass).
        var bundle = OrchestratorBundleBuilder.Build(ProducerNet());
        var result = new DeadlockFreeTest().Run(bundle);

        // Conservative: cb has no deadlock node, ss is truncated → Undecidable.
        Assert.Equal(TestResultStatus.Undecidable, result.Status);
        // But we DO have the cb tree wired into the bundle now.
        Assert.NotNull(bundle.CoverabilityTree);
    }

    // ─── Source-transition regression (T1 → P1 with no input on T1) ────────

    /// <summary>
    /// A single "source" transition with no input arcs, outputting to a
    /// place. P1 grows unboundedly. T1 is L4-live by construction (always
    /// enabled), so the net is live and deadlock-free.
    /// </summary>
    private static PetriNetSnapshot SourceTransitionNet() => new NetBuilder()
        .Place("P1")
        .Transition("T1")
        .Arc("T1", "P1")
        .Build();

    [Fact]
    public void Liveness_SourceTransitionUnboundedNet_IsLive()
    {
        // Pre-fix bug: net was trivially classified as Free-Choice (no shared
        // input places) and Commoner's theorem was applied — the siphon {P1}
        // has no marked trap → reported "Net is not live" (Fail). But the
        // theorem only holds for bounded FC nets, and T1 with no inputs is
        // structurally always-enabled → live by construction.
        var bundle = OrchestratorBundleBuilder.Build(SourceTransitionNet());
        var result = new LivenessTest().Run(bundle);

        Assert.Equal(TestResultStatus.Pass, result.Status);
    }

    [Fact]
    public void DeadlockFree_SourceTransitionUnboundedNet_IsPass()
    {
        // Pre-fix bug: with no IsDeadlock node in cb and Liveness = Fail
        // (false-fail from Commoner), DeadlockFree dropped to Undecidable.
        // T1's lack of input arcs structurally guarantees every reachable
        // marking has an enabled transition.
        var bundle = OrchestratorBundleBuilder.Build(SourceTransitionNet());
        var result = new DeadlockFreeTest().Run(bundle);

        Assert.Equal(TestResultStatus.Pass, result.Status);
    }

    /// <summary>
    /// Two-place producer-consumer where T_grow adds an extra token on each
    /// cycle: P1 → T_consume → P2, and T_grow consumes P1 but produces TWO
    /// tokens back to P1. P1 grows unboundedly, but every place has both
    /// producer and consumer arcs — the simple structural reversibility
    /// check (consumer-less place) doesn't fire. The new T-invariant check
    /// does: T_grow's net effect on P1 is +1, so it can't be in any
    /// T-invariant → not reversible.
    /// </summary>
    private static PetriNetSnapshot ImbalancedCycleNet() => new NetBuilder()
        .Place("P1", tokens: 1).Place("P2")
        .Transition("Tgrow").Transition("Tback")
        .Arc("P1",    "Tgrow")
        .Arc("Tgrow", "P1", weight: 2)   // net +1
        .Arc("P1",    "Tback")
        .Arc("Tback", "P2")
        .Build();

    [Fact]
    public void Reversibility_ImbalancedCycle_IsFailViaTInvariants()
    {
        // Cb tree has ω at P1. Both places have producer and consumer arcs,
        // so the structural "consumer-less place" check returns Undecidable.
        // The T-invariant check now runs even on unbounded ordinary nets and
        // finds that Tgrow isn't in any T-invariant (its net effect on P1
        // is non-zero), proving non-reversibility.
        var bundle = OrchestratorBundleBuilder.Build(ImbalancedCycleNet());
        var result = new ReversibilityTest().Run(bundle);

        Assert.True(bundle.IsUnbounded);
        Assert.False(bundle.Invariants!.WasSkipped,
            "Invariants must be computed for unbounded ordinary nets so ReversibilityTest can use T-invariant coverage.");
        Assert.Equal(TestResultStatus.Fail, result.Status);
    }

    [Fact]
    public void Reversibility_SourceTransitionUnboundedNet_IsFail()
    {
        // P1 only receives tokens (no consumer); once T1 fires we can never
        // get back to M0=[0]. Pre-fix code returned Undecidable from the
        // truncated state space; the new structural check finds the
        // consumer-less place + ω in cb tree and concludes Fail.
        var bundle = OrchestratorBundleBuilder.Build(SourceTransitionNet());
        var result = new ReversibilityTest().Run(bundle);

        Assert.Equal(TestResultStatus.Fail, result.Status);
    }

    [Fact]
    public void Boundedness_SourceTransitionUnboundedNet_IsFail()
    {
        // Sanity: cb tree introduces ω at P1, so Boundedness is definitively Fail.
        var bundle = OrchestratorBundleBuilder.Build(SourceTransitionNet());
        var result = new BoundednessTest().Run(bundle);

        Assert.Equal(TestResultStatus.Fail, result.Status);
    }

    [Fact]
    public void Safety_SourceTransitionUnboundedNet_IsFail()
    {
        // Sanity: ω in cb proves the net is not safe (P1 unbounded).
        var bundle = OrchestratorBundleBuilder.Build(SourceTransitionNet());
        var result = new SafetyTest().Run(bundle);

        Assert.Equal(TestResultStatus.Fail, result.Status);
    }

    // ─── Truncated-bounded accuracy tests ───────────────────────────────────
    //
    // A bounded net whose reachable state space exceeds the analysis cap
    // produces a TRUNCATED state space with NO ω-markings in the cb tree.
    // None of Boundedness, Safety, or Reversibility may claim a definitive
    // verdict in that case — they previously returned Fail with reasons like
    // "Net is unbounded" or "is not safe", which are flat-out wrong.

    /// <summary>
    /// 1-bounded counter: T1 takes a token from P_i and puts it in P_{i+1}.
    /// With N places it reaches exactly N markings — far more than the
    /// tiny cap we pass in. Bounded, safe, and reversible if we close
    /// the loop, none of which the truncated state space can prove.
    /// </summary>
    private static PetriNetSnapshot LargeBoundedNet(int placeCount)
    {
        var builder = new NetBuilder().Place("P0", tokens: 1);
        for (int i = 1; i < placeCount; i++) builder.Place($"P{i}");
        for (int i = 0; i < placeCount; i++)
        {
            int next = (i + 1) % placeCount;
            builder.Transition($"T{i}")
                   .Arc($"P{i}", $"T{i}")
                   .Arc($"T{i}", $"P{next}");
        }
        return builder.Build();
    }

    [Fact]
    public void Boundedness_TruncatedButBoundedNet_NeverClaimsFail()
    {
        // 20-place ring, cap=5 → cb is truncated with NO ω → cb branch
        // returns Undecidable, state-space branch returns Undecidable.
        // Invariants may still prove Pass (state machines are conservative).
        // The critical guarantee: we MUST NOT return Fail with reason
        // "net is unbounded" just because we ran out of state-space budget.
        var bundle = OrchestratorBundleBuilder.Build(LargeBoundedNet(20), cap: 5);
        var result = new BoundednessTest().Run(bundle);

        Assert.True(bundle.StateSpace!.IsTruncated);
        Assert.True(bundle.CoverabilityTree!.IsTruncated);
        Assert.False(bundle.IsUnbounded);
        Assert.NotEqual(TestResultStatus.Fail, result.Status);
    }

    [Fact]
    public void Safety_TruncatedButBoundedNet_NeverClaimsFail()
    {
        var bundle = OrchestratorBundleBuilder.Build(LargeBoundedNet(20), cap: 5);
        var result = new SafetyTest().Run(bundle);

        // Pre-fix code returned Fail ("net is unsafe") on truncation alone.
        // Classification (StateMachine + tokens ≤ 1) may still prove Pass,
        // but Fail is forbidden — we don't know what the unexplored markings
        // look like.
        Assert.NotEqual(TestResultStatus.Fail, result.Status);
    }

    [Fact]
    public void Reversibility_TruncatedStateSpace_IsUndecidable()
    {
        var bundle = OrchestratorBundleBuilder.Build(LargeBoundedNet(20), cap: 5);
        var result = new ReversibilityTest().Run(bundle);

        // Reversibility is decided purely from the state space. With
        // truncation we cannot know — the return-paths to M₀ may be in the
        // unexplored portion. Pre-fix code returned Fail here.
        Assert.Equal(TestResultStatus.Undecidable, result.Status);
    }

    // ─── Saturation-witness tests (ω-acceleration disabled) ─────────────────
    //
    // Inhibitor/reset arcs break Karp-Miller monotonicity, so the coverability
    // tree runs plain reachability WITHOUT ω-acceleration. An unbounded place
    // then grows until Fire() clamps it at the saturation ceiling
    // (int.MaxValue/2) and the tree truncates. Pre-fix, the absence of an exact
    // ω sentinel made Boundedness/Safety return Undecidable ("inconclusive")
    // even though saturation is a definite proof of unboundedness.

    /// <summary>
    /// Unbounded producer with a dummy inhibitor arc from an always-empty place.
    /// The inhibitor never blocks T1, so P1 grows without bound — but its
    /// presence disables ω-acceleration, forcing the saturation-ceiling path
    /// instead of an exact ω marking. The doubling output (weight ×2 with a net
    /// gain) makes P1 reach the saturation ceiling in a handful of firings, well
    /// before the marking cap, so the saturated witness actually materialises.
    /// </summary>
    private static PetriNetSnapshot SaturatingInhibitorNet() => new NetBuilder()
        .Place("P1", tokens: 1_000).Place("Pgate")       // Pgate stays empty forever
        .Transition("T1")
        // consume 1, produce a hefty fixed amount → P1 climbs by ~1e9/firing,
        // hitting the int.MaxValue/2 saturation ceiling within a few firings.
        .Arc("P1", "T1").Arc("T1", "P1", weight: 1_000_000_000)
        .Inhibitor("Pgate", "T1")                        // weight-1 inhibitor, never blocks
        .Build();

    [Fact]
    public void Bundle_SaturatedNet_IsRecognizedAsUnbounded()
    {
        var bundle = OrchestratorBundleBuilder.Build(SaturatingInhibitorNet(), cap: 500);

        // ω-acceleration is off (inhibitor present), so no exact ω sentinel,
        // but a saturated node must still flag the net as unbounded.
        Assert.True(bundle.IsUnbounded);
        Assert.Contains(bundle.CoverabilityTree!.Nodes,
            n => n.Marking.Any(CoverabilityTreeBuilder.GrowsWithoutBound));
        Assert.DoesNotContain(bundle.CoverabilityTree!.Nodes,
            n => n.Marking.Any(v => v == CoverabilityTreeBuilder.Omega));
    }

    [Fact]
    public void Boundedness_SaturatedNet_IsFailNotInconclusive()
    {
        var bundle = OrchestratorBundleBuilder.Build(SaturatingInhibitorNet(), cap: 500);
        var result = new BoundednessTest().Run(bundle);

        // The whole point of the fix: this must be a definite Fail, never
        // Undecidable, even though the tree truncated.
        Assert.Equal(TestResultStatus.Fail, result.Status);
    }

    [Fact]
    public void Safety_SaturatedNet_IsFailNotInconclusive()
    {
        var bundle = OrchestratorBundleBuilder.Build(SaturatingInhibitorNet(), cap: 500);
        var result = new SafetyTest().Run(bundle);

        Assert.Equal(TestResultStatus.Fail, result.Status);
    }

    [Fact]
    public void GrowsWithoutBound_RecognizesBothOmegaAndSaturation()
    {
        Assert.True(CoverabilityTreeBuilder.GrowsWithoutBound(CoverabilityTreeBuilder.Omega));
        Assert.True(CoverabilityTreeBuilder.GrowsWithoutBound(CoverabilityTreeBuilder.Saturated));
        Assert.False(CoverabilityTreeBuilder.GrowsWithoutBound(CoverabilityTreeBuilder.Saturated - 1));
        Assert.False(CoverabilityTreeBuilder.GrowsWithoutBound(0));
        Assert.False(CoverabilityTreeBuilder.GrowsWithoutBound(1_000_000));
    }

    [Fact]
    public void Conservativeness_DefinitivelyUnbounded_HiddenByUI()
    {
        // Conservativeness / Repetitiveness on unbounded ordinary nets are now
        // actually computed and may return Fail (P1 isn't covered by any
        // P-invariant in a producer net). The UI hides them via the
        // IsDefinitelyUnbounded flag — this test verifies the flag is set so
        // the UI suppression engages, regardless of the engine verdict.
        var bundle = OrchestratorBundleBuilder.Build(ProducerNet());

        Assert.True(bundle.IsUnbounded,
            "Bundle must signal definitely-unbounded so the UI can suppress these properties.");
    }
}
