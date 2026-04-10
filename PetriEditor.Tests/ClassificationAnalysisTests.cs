using Analysis;
using Analysis.Engines;
using PetriEditor.Tests.Helpers;

namespace PetriEditor.Tests;

public class ClassificationAnalysisTests
{
    private static ClassificationAnalysis Compute(PetriNetSnapshot net)
    {
        var a = new ClassificationAnalysis();
        a.Compute(net);
        return a;
    }

    // ── Error conditions ──────────────────────────────────────────────────

    [Fact]
    public void EmptyNet_ReturnsError()
    {
        var net = new NetBuilder().Build();
        var a = Compute(net);
        Assert.True(a.HasErrors);
    }

    // ── Ordinary ─────────────────────────────────────────────────────────

    [Fact]
    public void AllUnitNormalArcs_IsOrdinary()
    {
        var net = new NetBuilder()
            .Place("P1", tokens: 1).Place("P2")
            .Transition("T1")
            .Arc("P1", "T1").Arc("T1", "P2")
            .Build();

        var a = Compute(net);

        Assert.True(a.IsOfType(NetSubclass.Ordinary));
    }

    [Fact]
    public void WeightedArc_NotOrdinary()
    {
        var net = new NetBuilder()
            .Place("P1", tokens: 2).Place("P2")
            .Transition("T1")
            .Arc("P1", "T1", weight: 2).Arc("T1", "P2")
            .Build();

        var a = Compute(net);

        Assert.False(a.IsOfType(NetSubclass.Ordinary));
    }

    [Fact]
    public void InhibitorArc_NotOrdinary()
    {
        var net = new NetBuilder()
            .Place("P1").Place("P2", tokens: 1).Place("P3")
            .Transition("T1")
            .Inhibitor("P1", "T1")
            .Arc("P2", "T1").Arc("T1", "P3")
            .Build();

        var a = Compute(net);

        Assert.False(a.IsOfType(NetSubclass.Ordinary));
    }

    // ── State Machine ─────────────────────────────────────────────────────

    [Fact]
    public void SimpleLoop_IsStateMachine()
    {
        // Each transition has exactly 1 input and 1 output place
        var net = new NetBuilder()
            .Place("P1", tokens: 1).Place("P2")
            .Transition("T1").Transition("T2")
            .Arc("P1", "T1").Arc("T1", "P2")
            .Arc("P2", "T2").Arc("T2", "P1")
            .Build();

        var a = Compute(net);

        Assert.True(a.IsOfType(NetSubclass.StateMachine));
    }

    [Fact]
    public void TwoInputsToTransition_NotStateMachine()
    {
        // T1 has 2 input places
        var net = new NetBuilder()
            .Place("P1", tokens: 1).Place("P2", tokens: 1).Place("P3")
            .Transition("T1")
            .Arc("P1", "T1").Arc("P2", "T1").Arc("T1", "P3")
            .Build();

        var a = Compute(net);

        Assert.False(a.IsOfType(NetSubclass.StateMachine));
    }

    [Fact]
    public void TwoOutputsFromTransition_NotStateMachine()
    {
        var net = new NetBuilder()
            .Place("P1", tokens: 1).Place("P2").Place("P3")
            .Transition("T1")
            .Arc("P1", "T1").Arc("T1", "P2").Arc("T1", "P3")
            .Build();

        var a = Compute(net);

        Assert.False(a.IsOfType(NetSubclass.StateMachine));
    }

    // ── Marked Graph ──────────────────────────────────────────────────────

    [Fact]
    public void EachPlaceOneInOneOut_IsMarkedGraph()
    {
        // Ring: P1->T1->P2->T2->P3->T3->P1
        var net = new NetBuilder()
            .Place("P1", tokens: 1).Place("P2").Place("P3")
            .Transition("T1").Transition("T2").Transition("T3")
            .Arc("P1", "T1").Arc("T1", "P2")
            .Arc("P2", "T2").Arc("T2", "P3")
            .Arc("P3", "T3").Arc("T3", "P1")
            .Build();

        var a = Compute(net);

        Assert.True(a.IsOfType(NetSubclass.MarkedGraph));
    }

    [Fact]
    public void PlaceWithTwoOutputs_NotMarkedGraph()
    {
        // P1 has two outgoing transitions
        var net = new NetBuilder()
            .Place("P1", tokens: 1).Place("P2").Place("P3")
            .Transition("T1").Transition("T2")
            .Arc("P1", "T1").Arc("T1", "P2")
            .Arc("P1", "T2").Arc("T2", "P3")
            .Build();

        var a = Compute(net);

        Assert.False(a.IsOfType(NetSubclass.MarkedGraph));
    }

    // ── Free Choice ───────────────────────────────────────────────────────

    [Fact]
    public void NoSharedInputPlaces_IsFreeChoice()
    {
        // T1 and T2 have disjoint inputs — vacuously free-choice
        var net = new NetBuilder()
            .Place("P1", tokens: 1).Place("P2", tokens: 1).Place("P3").Place("P4")
            .Transition("T1").Transition("T2")
            .Arc("P1", "T1").Arc("T1", "P3")
            .Arc("P2", "T2").Arc("T2", "P4")
            .Build();

        var a = Compute(net);

        Assert.True(a.IsOfType(NetSubclass.FreeChoice));
    }

    [Fact]
    public void SharedInputPlace_SamePreset_IsFreeChoice()
    {
        // T1 and T2 both read from P1 only — pre(T1)=pre(T2)={P1}
        var net = new NetBuilder()
            .Place("P1", tokens: 1).Place("P2").Place("P3")
            .Transition("T1").Transition("T2")
            .Arc("P1", "T1").Arc("T1", "P2")
            .Arc("P1", "T2").Arc("T2", "P3")
            .Build();

        var a = Compute(net);

        Assert.True(a.IsOfType(NetSubclass.FreeChoice));
    }

    [Fact]
    public void SharedInputPlace_DifferentPresets_NotFreeChoice()
    {
        // T1 reads P1; T2 reads P1 AND P2 — pre(T1) ≠ pre(T2) but they overlap
        var net = new NetBuilder()
            .Place("P1", tokens: 1).Place("P2", tokens: 1).Place("P3").Place("P4")
            .Transition("T1").Transition("T2")
            .Arc("P1", "T1").Arc("T1", "P3")
            .Arc("P1", "T2").Arc("P2", "T2").Arc("T2", "P4")
            .Build();

        var a = Compute(net);

        Assert.False(a.IsOfType(NetSubclass.FreeChoice));
    }

    // ── Extended Free Choice ──────────────────────────────────────────────

    [Fact]
    public void SharedInputPlaceAllTransitionsHaveSingleInput_IsEFC()
    {
        // Both T1 and T2 have exactly 1 input (P1), which they share
        var net = new NetBuilder()
            .Place("P1", tokens: 1).Place("P2").Place("P3")
            .Transition("T1").Transition("T2")
            .Arc("P1", "T1").Arc("T1", "P2")
            .Arc("P1", "T2").Arc("T2", "P3")
            .Build();

        var a = Compute(net);

        Assert.True(a.IsOfType(NetSubclass.ExtendedFreeChoice));
    }

    [Fact]
    public void SharedInputPlace_TransitionHasMultipleInputs_NotEFC()
    {
        var net = new NetBuilder()
            .Place("P1", tokens: 1).Place("P2", tokens: 1).Place("P3").Place("P4")
            .Transition("T1").Transition("T2")
            .Arc("P1", "T1").Arc("T1", "P3")
            .Arc("P1", "T2").Arc("P2", "T2").Arc("T2", "P4")
            .Build();

        var a = Compute(net);

        Assert.False(a.IsOfType(NetSubclass.ExtendedFreeChoice));
    }

    // ── StateMachine is also FreeChoice and EFC ───────────────────────────

    [Fact]
    public void StateMachine_IsAlsoFreeChoiceAndEFC()
    {
        var net = new NetBuilder()
            .Place("P1", tokens: 1).Place("P2")
            .Transition("T1").Transition("T2")
            .Arc("P1", "T1").Arc("T1", "P2")
            .Arc("P2", "T2").Arc("T2", "P1")
            .Build();

        var a = Compute(net);

        Assert.True(a.IsOfType(NetSubclass.StateMachine));
        Assert.True(a.IsOfType(NetSubclass.FreeChoice));
        Assert.True(a.IsOfType(NetSubclass.ExtendedFreeChoice));
    }

    // ── Summary text ─────────────────────────────────────────────────────

    [Fact]
    public void Summary_IncludesStateMachineLabel()
    {
        var net = new NetBuilder()
            .Place("P1", tokens: 1).Place("P2")
            .Transition("T1").Transition("T2")
            .Arc("P1", "T1").Arc("T1", "P2")
            .Arc("P2", "T2").Arc("T2", "P1")
            .Build();

        var a = Compute(net);

        Assert.Contains("State Machine", a.Summary());
    }

    [Fact]
    public void Summary_ErrorNet_ReturnsErrorMessage()
    {
        var a = Compute(new NetBuilder().Build());
        // Summary() returns the ErrorMsg directly when HasErrors is true
        Assert.Equal(a.ErrorMsg, a.Summary());
        Assert.False(string.IsNullOrEmpty(a.Summary()));
    }
}
