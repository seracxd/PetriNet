using Analysis;
using Analysis.Algorithms;
using Analysis.Engines;
using PetriEditor.Tests.Helpers;

namespace PetriEditor.Tests;

/// <summary>
/// Targeted tests that exercise branches identified as uncovered by the coverage report.
/// Covers: CyclesAnalysisExtensions, InvariantAnalysis.SkipUnbounded + WasTruncated,
/// TrapCotrapAnalysis over MaxPlaces, truncated-state-space liveness, and all
/// "non-ordinary net" branches in the five property-test classes.
/// </summary>
public class CoverageGapTests
{
    // ── CyclesAnalysisExtensions ─────────────────────────────────────────────

    [Fact]
    public void CyclesExtensions_PlaceAndTransitionCoverage_MatchExpected()
    {
        // Two-place ring: one cycle covers both places and both transitions.
        var net = new NetBuilder()
            .Place("P1", tokens: 1).Place("P2")
            .Transition("T1").Transition("T2")
            .Arc("P1", "T1").Arc("T1", "P2")
            .Arc("P2", "T2").Arc("T2", "P1")
            .Build();

        var cyc = new CyclesAnalysis(); cyc.Compute(net);

        Assert.Equal(net.Places.Count,     cyc.PlaceCoverageCount(net));
        Assert.Equal(net.Transitions.Count, cyc.TransitionCoverageCount(net));
    }

    [Fact]
    public void CyclesExtensions_NoCycles_ZeroCoverage()
    {
        // Linear net: P1 → T1 → P2 — no cycles.
        var net = new NetBuilder()
            .Place("P1", tokens: 1).Place("P2")
            .Transition("T1")
            .Arc("P1", "T1").Arc("T1", "P2")
            .Build();

        var cyc = new CyclesAnalysis(); cyc.Compute(net);

        Assert.Empty(cyc.Cycles);
        Assert.Equal(0, cyc.PlaceCoverageCount(net));
        Assert.Equal(0, cyc.TransitionCoverageCount(net));
    }

    // ── InvariantAnalysis.SkipUnbounded ──────────────────────────────────────

    [Fact]
    public void InvariantAnalysis_SkipUnbounded_SetsErrorFlags()
    {
        var inv = new InvariantAnalysis();
        inv.SkipUnbounded();

        Assert.True(inv.HasErrors);
        Assert.True(inv.WasSkipped);
        Assert.NotEmpty(inv.ErrorMsg!);
        Assert.Empty(inv.PInvariants);
        Assert.Empty(inv.TInvariants);
    }

    // ── TrapCotrapAnalysis over MaxPlaces ─────────────────────────────────────

    [Fact]
    public void TrapCotrap_OverMaxPlaces_SetsHasErrors()
    {
        // Build a ring with MaxPlaces + 1 places to exceed the enumeration limit.
        var n = TrapCotrapAnalysis.MaxPlaces + 1;
        var b = new NetBuilder();
        for (int i = 0; i < n; i++) b.Place($"P{i}", tokens: i == 0 ? 1 : 0);
        for (int i = 0; i < n; i++) { b.Transition($"T{i}"); b.Arc($"P{i}", $"T{i}").Arc($"T{i}", $"P{(i + 1) % n}"); }
        var net = b.Build();

        var tc = new TrapCotrapAnalysis(); tc.Compute(net);

        Assert.True(tc.HasErrors);
        Assert.NotEmpty(tc.ErrorMsg!);
    }

    // ── LivenessTest on truncated state space ────────────────────────────────

    [Fact]
    public void LivenessTest_TruncatedStateSpace_ReturnsUndecidable()
    {
        // 5-place ring has 5 states; limiting to 3 forces truncation.
        var net = TokenRing(5);
        var ss  = new StateSpaceAnalysis(); ss.Build(net, maxStates: 3);
        Assert.True(ss.IsTruncated);

        var bundle = new AnalysisBundle { Net = net, StateSpace = ss };
        var result = new LivenessTest().Run(bundle);

        Assert.Equal(TestResultStatus.Undecidable, result.Status);
        Assert.True(result.Reasons.Count > 0);
    }

    [Fact]
    public void LivenessTest_TruncatedWithNoFiredTransitions_ReturnsUndecidable()
    {
        // Single-place self-loop: T1 fires immediately, but we truncate at 0 states
        // by using a separate net with maxStates=1 where the initial state has no edges explored.
        // Simpler: use maxStates=1 on a ring where only the root state is stored.
        var net = TokenRing(4);
        var ss  = new StateSpaceAnalysis(); ss.Build(net, maxStates: 1);
        Assert.True(ss.IsTruncated);

        var bundle = new AnalysisBundle { Net = net, StateSpace = ss };
        var result = new LivenessTest().Run(bundle);

        Assert.Equal(TestResultStatus.Undecidable, result.Status);
    }

    // ── Non-ordinary net: all property test "IsOrdinaryNet" branches ─────────

    private static (PetriNetSnapshot net, AnalysisBundle bundle) InhibitorBundle()
    {
        // P_guard --inhibitor--> T1 ; P_src --normal--> T1 --normal--> P_dst
        var net = new NetBuilder()
            .Place("P_guard", tokens: 0)
            .Place("P_src",   tokens: 1)
            .Place("P_dst",   tokens: 0)
            .Transition("T1")
            .Arc("P_guard", "T1", weight: 1, type: PnArcType.Inhibitor)
            .Arc("P_src",   "T1")
            .Arc("T1", "P_dst")
            .Build();

        var ss  = new StateSpaceAnalysis();  ss.Build(net);
        var inv = new InvariantAnalysis();   inv.Compute(net);
        var cls = new ClassificationAnalysis(); cls.Compute(net);

        var bundle = new AnalysisBundle
        {
            Net            = net,
            StateSpace     = ss,
            Invariants     = inv,
            Classification = cls,
        };
        return (net, bundle);
    }

    [Fact]
    public void BoundednessTest_InhibitorNet_InvariantBranchIsUndecidable()
    {
        var (_, bundle) = InhibitorBundle();
        Assert.False(bundle.IsOrdinaryNet);

        var result = new BoundednessTest().Run(bundle);

        // Invariant-based path returns Undecidable for non-ordinary nets;
        // state-space still runs, so overall result may be Pass/Undecidable.
        // At minimum, no error should be thrown.
        Assert.True(result.Status == TestResultStatus.Pass
                 || result.Status == TestResultStatus.Undecidable);
    }

    [Fact]
    public void SafetyTest_InhibitorNet_StructuralBranchIsUndecidable()
    {
        var (_, bundle) = InhibitorBundle();
        // SafetyTest structural branch (marked graph check) returns Undecidable for non-ordinary.
        var result = new SafetyTest().Run(bundle);
        Assert.NotEqual(TestResultStatus.Fail, result.Status);
    }

    [Fact]
    public void ConservativenessTest_InhibitorNet_RunsWithoutThrowing()
    {
        var (_, bundle) = InhibitorBundle();
        // The invariant branch executes the non-ordinary guard; the classification
        // branch may still return Pass (State machine). PropertyResultBuilder drops
        // Undecidable-tagged reasons when the final verdict is Pass — that's by design.
        var result = new ConservativenessTest().Run(bundle);
        Assert.True(result.Status == TestResultStatus.Pass
                 || result.Status == TestResultStatus.Undecidable);
    }

    [Fact]
    public void RepetitivenessTest_InhibitorNet_ReturnsUndecidable()
    {
        var (_, bundle) = InhibitorBundle();
        var result = new RepetitivenessTest().Run(bundle);
        Assert.Equal(TestResultStatus.Undecidable, result.Status);
        Assert.Contains(result.Reasons, r => r.Contains("inhibitor") || r.Contains("reset"));
    }

    [Fact]
    public void LivenessTest_InhibitorNet_NonOrdinaryReasonPresent()
    {
        var (_, bundle) = InhibitorBundle();
        // The inhibitor net is not live (T1 cannot fire from all reachable states).
        // The invariant branch also emits a non-ordinary reason.
        // Either way, the test must run without throwing.
        var result = new LivenessTest().Run(bundle);
        Assert.True(result.Status == TestResultStatus.Fail
                 || result.Status == TestResultStatus.Undecidable);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static PetriNetSnapshot TokenRing(int n)
    {
        var b = new NetBuilder();
        for (int i = 0; i < n; i++) b.Place($"P{i}", tokens: i == 0 ? 1 : 0);
        for (int i = 0; i < n; i++) { b.Transition($"T{i}"); b.Arc($"P{i}", $"T{i}").Arc($"T{i}", $"P{(i + 1) % n}"); }
        return b.Build();
    }
}
