using Analysis;
using Analysis.Algorithms;
using Analysis.Engines;
using PetriEditor.Tests.Helpers;

namespace PetriEditor.Tests;

/// <summary>
/// Tests for all seven property-test classes.
/// Each test builds a full AnalysisBundle from a simple net so the property
/// test has all engine results available.
/// </summary>
public class PropertyTestsTests
{
    // ── Bundle helpers ────────────────────────────────────────────────────

    private static AnalysisBundle BuildBundle(PetriNetSnapshot net, bool includeAll = true)
    {
        var ss  = new StateSpaceAnalysis();
        ss.Build(net);

        var inv = new InvariantAnalysis();
        inv.Compute(net);

        var cls = new ClassificationAnalysis();
        cls.Compute(net);

        var cyc = new CyclesAnalysis();
        cyc.Compute(net);

        var tc = new TrapCotrapAnalysis();
        tc.Compute(net);

        return new AnalysisBundle
        {
            Net            = net,
            StateSpace     = ss,
            Invariants     = inv,
            Classification = cls,
            Cycles         = cyc,
            TrapCotrap     = tc,
        };
    }

    // ── Liveness ──────────────────────────────────────────────────────────

    [Fact]
    public void Liveness_LiveNet_Pass()
    {
        // Strongly connected loop — both transitions always reachable
        var net = new NetBuilder()
            .Place("P1", tokens: 1).Place("P2")
            .Transition("T1").Transition("T2")
            .Arc("P1", "T1").Arc("T1", "P2")
            .Arc("P2", "T2").Arc("T2", "P1")
            .Build();

        var bundle = BuildBundle(net);
        var result = new LivenessTest().Run(bundle);

        Assert.Equal(TestResultStatus.Pass, result.Status);
    }

    [Fact]
    public void Liveness_DeadTransition_Fail()
    {
        // T2 can never fire (P3 never gets a token)
        var net = new NetBuilder()
            .Place("P1", tokens: 1).Place("P2").Place("P3")
            .Transition("T1").Transition("T2")
            .Arc("P1", "T1").Arc("T1", "P2")
            .Arc("P3", "T2").Arc("T2", "P1")
            .Build();

        var bundle = BuildBundle(net);
        var result = new LivenessTest().Run(bundle);

        Assert.Equal(TestResultStatus.Fail, result.Status);
    }

    [Fact]
    public void Liveness_NullStateSpace_Undecidable()
    {
        var net = new NetBuilder()
            .Place("P1", tokens: 1).Place("P2")
            .Transition("T1")
            .Arc("P1", "T1").Arc("T1", "P2")
            .Build();

        var bundle = new AnalysisBundle { Net = net }; // no engines
        var result = new LivenessTest().Run(bundle);

        Assert.Equal(TestResultStatus.Undecidable, result.Status);
    }

    // ── Boundedness ───────────────────────────────────────────────────────

    [Fact]
    public void Boundedness_BoundedNet_Pass()
    {
        var net = new NetBuilder()
            .Place("P1", tokens: 1).Place("P2")
            .Transition("T1").Transition("T2")
            .Arc("P1", "T1").Arc("T1", "P2")
            .Arc("P2", "T2").Arc("T2", "P1")
            .Build();

        var bundle = BuildBundle(net);
        var result = new BoundednessTest().Run(bundle);

        Assert.Equal(TestResultStatus.Pass, result.Status);
    }

    [Fact]
    public void Boundedness_UnboundedNet_Fail()
    {
        // T1 always adds a token — unbounded
        var net = new NetBuilder()
            .Place("P1", tokens: 1)
            .Transition("T1")
            .Arc("P1", "T1").Arc("T1", "P1").Arc("T1", "P1") // net +1
            .Build();

        var ss = new StateSpaceAnalysis();
        ss.Build(net, maxStates: 50);

        var bundle = new AnalysisBundle { Net = net, StateSpace = ss };
        var result = new BoundednessTest().Run(bundle);

        Assert.Equal(TestResultStatus.Fail, result.Status);
    }

    // ── Safety ────────────────────────────────────────────────────────────

    [Fact]
    public void Safety_SafeNet_Pass()
    {
        // At most 1 token in each place at any time
        var net = new NetBuilder()
            .Place("P1", tokens: 1).Place("P2")
            .Transition("T1").Transition("T2")
            .Arc("P1", "T1").Arc("T1", "P2")
            .Arc("P2", "T2").Arc("T2", "P1")
            .Build();

        var bundle = BuildBundle(net);
        var result = new SafetyTest().Run(bundle);

        Assert.Equal(TestResultStatus.Pass, result.Status);
    }

    [Fact]
    public void Safety_UnsafeNet_Fail()
    {
        // Two places merge into T1, producing 3 tokens in P3 — not a StateMachine,
        // so the classification branch can't override the state-space Fail verdict.
        var net = new NetBuilder()
            .Place("P1", tokens: 1).Place("P2", tokens: 1).Place("P3")
            .Transition("T1")
            .Arc("P1", "T1").Arc("P2", "T1").Arc("T1", "P3", weight: 3)
            .Build();

        var bundle = BuildBundle(net);
        var result = new SafetyTest().Run(bundle);

        Assert.Equal(TestResultStatus.Fail, result.Status);
    }

    // ── Conservativeness ──────────────────────────────────────────────────

    [Fact]
    public void Conservativeness_ConservativeNet_Pass()
    {
        var net = new NetBuilder()
            .Place("P1", tokens: 1).Place("P2")
            .Transition("T1").Transition("T2")
            .Arc("P1", "T1").Arc("T1", "P2")
            .Arc("P2", "T2").Arc("T2", "P1")
            .Build();

        var bundle = BuildBundle(net);
        var result = new ConservativenessTest().Run(bundle);

        Assert.Equal(TestResultStatus.Pass, result.Status);
    }

    [Fact]
    public void Conservativeness_NonConservativeNet_Fail()
    {
        // No P-invariant: T1 net-produces
        var net = new NetBuilder()
            .Place("P1", tokens: 1).Place("P2")
            .Transition("T1")
            .Arc("P1", "T1")
            .Arc("T1", "P1").Arc("T1", "P2") // +1 to P2 with no matching sink
            .Build();

        var bundle = BuildBundle(net);
        var result = new ConservativenessTest().Run(bundle);

        // Not all places covered by a P-invariant
        Assert.Equal(TestResultStatus.Fail, result.Status);
    }

    // ── Repetitiveness ────────────────────────────────────────────────────

    [Fact]
    public void Repetitiveness_RepetitiveNet_Pass()
    {
        // Simple cycle has T-invariant covering all transitions
        var net = new NetBuilder()
            .Place("P1", tokens: 1).Place("P2")
            .Transition("T1").Transition("T2")
            .Arc("P1", "T1").Arc("T1", "P2")
            .Arc("P2", "T2").Arc("T2", "P1")
            .Build();

        var bundle = BuildBundle(net);
        var result = new RepetitivenessTest().Run(bundle);

        Assert.Equal(TestResultStatus.Pass, result.Status);
    }

    [Fact]
    public void Repetitiveness_SourceTransition_Fail()
    {
        // T1 is a source (no input); its column in W is all +, so no T-invariant
        var net = new NetBuilder()
            .Place("P1").Place("P2")
            .Transition("T1").Transition("T2")
            .Arc("T1", "P1")               // T1 source
            .Arc("P1", "T2").Arc("T2", "P2")
            .Build();

        var bundle = BuildBundle(net);
        var result = new RepetitivenessTest().Run(bundle);

        Assert.Equal(TestResultStatus.Fail, result.Status);
    }

    // ── DeadlockFree ──────────────────────────────────────────────────────

    [Fact]
    public void DeadlockFree_LiveNet_Pass()
    {
        var net = new NetBuilder()
            .Place("P1", tokens: 1).Place("P2")
            .Transition("T1").Transition("T2")
            .Arc("P1", "T1").Arc("T1", "P2")
            .Arc("P2", "T2").Arc("T2", "P1")
            .Build();

        var bundle = BuildBundle(net);
        var result = new DeadlockFreeTest().Run(bundle);

        Assert.Equal(TestResultStatus.Pass, result.Status);
    }

    [Fact]
    public void DeadlockFree_NetWithDeadlock_Fail()
    {
        // After T1 fires, P2 has a token but T2 has no input — deadlock
        var net = new NetBuilder()
            .Place("P1", tokens: 1).Place("P2")
            .Transition("T1")
            .Arc("P1", "T1").Arc("T1", "P2")
            .Build();

        var bundle = BuildBundle(net);
        var result = new DeadlockFreeTest().Run(bundle);

        Assert.Equal(TestResultStatus.Fail, result.Status);
    }

    // ── Reversibility ────────────────────────────────────────────────────

    [Fact]
    public void Reversibility_StronglyConnectedStateSpace_Pass()
    {
        var net = new NetBuilder()
            .Place("P1", tokens: 1).Place("P2")
            .Transition("T1").Transition("T2")
            .Arc("P1", "T1").Arc("T1", "P2")
            .Arc("P2", "T2").Arc("T2", "P1")
            .Build();

        var bundle = BuildBundle(net);
        var result = new ReversibilityTest().Run(bundle);

        Assert.Equal(TestResultStatus.Pass, result.Status);
    }

    [Fact]
    public void Reversibility_IrreversibleNet_Fail()
    {
        // Once T1 fires (P1→P2), T2 can fire (P2→P3), but can't return to M0
        var net = new NetBuilder()
            .Place("P1", tokens: 1).Place("P2").Place("P3")
            .Transition("T1").Transition("T2")
            .Arc("P1", "T1").Arc("T1", "P2")
            .Arc("P2", "T2").Arc("T2", "P3")
            .Build();

        var bundle = BuildBundle(net);
        var result = new ReversibilityTest().Run(bundle);

        Assert.Equal(TestResultStatus.Fail, result.Status);
    }

    // ── Result structure ──────────────────────────────────────────────────

    [Fact]
    public void AllResults_HaveNonEmptyReasonsList()
    {
        var net = new NetBuilder()
            .Place("P1", tokens: 1).Place("P2")
            .Transition("T1").Transition("T2")
            .Arc("P1", "T1").Arc("T1", "P2")
            .Arc("P2", "T2").Arc("T2", "P1")
            .Build();

        var bundle = BuildBundle(net);

        Assert.NotEmpty(new LivenessTest().Run(bundle).Reasons);
        Assert.NotEmpty(new BoundednessTest().Run(bundle).Reasons);
        Assert.NotEmpty(new SafetyTest().Run(bundle).Reasons);
        Assert.NotEmpty(new ConservativenessTest().Run(bundle).Reasons);
        Assert.NotEmpty(new RepetitivenessTest().Run(bundle).Reasons);
        Assert.NotEmpty(new DeadlockFreeTest().Run(bundle).Reasons);
        Assert.NotEmpty(new ReversibilityTest().Run(bundle).Reasons);
    }

    [Fact]
    public void ResultProperty_MatchesTestClass()
    {
        var net = new NetBuilder()
            .Place("P1", tokens: 1).Place("P2")
            .Transition("T1").Transition("T2")
            .Arc("P1", "T1").Arc("T1", "P2")
            .Arc("P2", "T2").Arc("T2", "P1")
            .Build();
        var bundle = BuildBundle(net);

        Assert.Equal(NetProperty.Liveness,         new LivenessTest().Run(bundle).Property);
        Assert.Equal(NetProperty.Boundedness,       new BoundednessTest().Run(bundle).Property);
        Assert.Equal(NetProperty.Safety,            new SafetyTest().Run(bundle).Property);
        Assert.Equal(NetProperty.Conservativeness,  new ConservativenessTest().Run(bundle).Property);
        Assert.Equal(NetProperty.Repetitiveness,    new RepetitivenessTest().Run(bundle).Property);
        Assert.Equal(NetProperty.DeadlockFree,      new DeadlockFreeTest().Run(bundle).Property);
        Assert.Equal(NetProperty.Reversibility,     new ReversibilityTest().Run(bundle).Property);
    }

    // ── DeadlockFree derives from Liveness ───────────────────────────────

    [Fact]
    public void DeadlockFree_UsesLivenessResult_WhenStateSpaceTruncated()
    {
        // Build a state space that is truncated so IsDeadlockFree() is unreliable,
        // but pre-populate the liveness result as Pass so DeadlockFree derives from it.
        var net = new NetBuilder()
            .Place("P1", tokens: 1).Place("P2")
            .Transition("T1").Transition("T2")
            .Arc("P1", "T1").Arc("T1", "P2")
            .Arc("P2", "T2").Arc("T2", "P1")
            .Build();

        var ss = new StateSpaceAnalysis();
        ss.Build(net, maxStates: 1); // force truncation

        var bundle = new AnalysisBundle
        {
            Net        = net,
            StateSpace = ss,
        };

        // Pre-populate a passing liveness result so DeadlockFree can derive from it
        var livenessBuilder = new PropertyResultBuilder(NetProperty.Liveness);
        livenessBuilder.AddReason("Manually set live.", TestResultStatus.Pass);
        livenessBuilder.SetStatus(TestResultStatus.Pass);
        var livenessResult = livenessBuilder.Build();
        bundle.PropertyResults[NetProperty.Liveness] = livenessResult;

        var result = new DeadlockFreeTest().Run(bundle);

        // Should pass via the liveness derivation
        Assert.Equal(TestResultStatus.Pass, result.Status);
    }
}
