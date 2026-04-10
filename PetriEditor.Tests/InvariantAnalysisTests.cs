using Analysis;
using Analysis.Engines;
using PetriEditor.Tests.Helpers;

namespace PetriEditor.Tests;

public class InvariantAnalysisTests
{
    private static InvariantAnalysis Compute(PetriNetSnapshot net)
    {
        var a = new InvariantAnalysis();
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

    // ── P-invariants ──────────────────────────────────────────────────────

    [Fact]
    public void StateMachine_HasPInvariant_CoveringAllPlaces()
    {
        // Simple loop: P1->T1->P2->T2->P1
        // This is a conservative net — total tokens preserved
        var net = new NetBuilder()
            .Place("P1", tokens: 1).Place("P2")
            .Transition("T1").Transition("T2")
            .Arc("P1", "T1").Arc("T1", "P2")
            .Arc("P2", "T2").Arc("T2", "P1")
            .Build();

        var a = Compute(net);

        Assert.False(a.HasErrors);
        Assert.True(a.PInvariants.Count > 0);

        // At least one P-invariant should cover both places
        bool coversAll = a.PInvariants.Any(inv =>
            inv.Covers("P1") && inv.Covers("P2"));
        Assert.True(coversAll);
    }

    [Fact]
    public void ProducerNet_NoPInvariant()
    {
        // T1 always adds a token — no conservation, so no P-invariant
        var net = new NetBuilder()
            .Place("P1", tokens: 1)
            .Transition("T1")
            .Arc("P1", "T1").Arc("T1", "P1").Arc("T1", "P1") // net +1
            .Build();

        var a = Compute(net);

        // P-invariants encode token conservation; this net has none
        // (incidence col for T1 is +1, so y·(+1)=0 has no positive solution)
        Assert.Empty(a.PInvariants);
    }

    [Fact]
    public void TwoConservativePlaces_PInvariantCoversAll()
    {
        // P1+P2 = const;  P1->T1->P2, P2->T2->P1
        var net = new NetBuilder()
            .Place("P1", tokens: 1).Place("P2").Place("P3", tokens: 1)
            .Transition("T1").Transition("T2").Transition("T3")
            .Arc("P1", "T1").Arc("T1", "P2")
            .Arc("P2", "T2").Arc("T2", "P1")
            .Arc("P3", "T3").Arc("T3", "P3") // self-loop on P3
            .Build();

        var a = Compute(net);

        Assert.False(a.HasErrors);
        Assert.True(a.PInvariants.Count >= 1);
    }

    // ── T-invariants ──────────────────────────────────────────────────────

    [Fact]
    public void StateMachine_HasTInvariant_CoveringAllTransitions()
    {
        // P1->T1->P2->T2->P1: firing T1 then T2 returns to initial marking
        var net = new NetBuilder()
            .Place("P1", tokens: 1).Place("P2")
            .Transition("T1").Transition("T2")
            .Arc("P1", "T1").Arc("T1", "P2")
            .Arc("P2", "T2").Arc("T2", "P1")
            .Build();

        var a = Compute(net);

        Assert.False(a.HasErrors);
        Assert.True(a.TInvariants.Count > 0);

        bool coversAll = a.TInvariants.Any(inv =>
            inv.Covers("T1") && inv.Covers("T2"));
        Assert.True(coversAll);
    }

    [Fact]
    public void OpenNet_NoTInvariant()
    {
        // A source transition that only produces — can't return to initial
        var net = new NetBuilder()
            .Place("P1").Place("P2")
            .Transition("T1").Transition("T2")
            .Arc("T1", "P1")      // T1 is a source (no input)
            .Arc("P1", "T2").Arc("T2", "P2")
            .Build();

        var a = Compute(net);

        // T1 has no input; its column in W is all positive → no T-invariant covers it
        Assert.DoesNotContain(a.TInvariants, inv => inv.Covers("T1"));
    }

    // ── Invariant structure ───────────────────────────────────────────────

    [Fact]
    public void Invariant_OnlyPositiveCoefficients()
    {
        var net = new NetBuilder()
            .Place("P1", tokens: 1).Place("P2")
            .Transition("T1").Transition("T2")
            .Arc("P1", "T1").Arc("T1", "P2")
            .Arc("P2", "T2").Arc("T2", "P1")
            .Build();

        var a = Compute(net);

        foreach (var inv in a.PInvariants)
            Assert.All(inv.Structure.Values, v => Assert.True(v > 0));

        foreach (var inv in a.TInvariants)
            Assert.All(inv.Structure.Values, v => Assert.True(v > 0));
    }

    [Fact]
    public void Invariant_Covers_ReturnsFalse_ForAbsentId()
    {
        var net = new NetBuilder()
            .Place("P1", tokens: 1).Place("P2")
            .Transition("T1").Transition("T2")
            .Arc("P1", "T1").Arc("T1", "P2")
            .Arc("P2", "T2").Arc("T2", "P1")
            .Build();

        var a = Compute(net);

        foreach (var inv in a.PInvariants)
            Assert.False(inv.Covers("nonexistent_id"));
    }

    // ── Three-place conservation net ─────────────────────────────────────

    [Fact]
    public void ThreePlaceLoop_PInvariantWithCoefficients()
    {
        // P1->T1->P2->T2->P3->T3->P1 : all unit, so y=(1,1,1) is a P-invariant
        var net = new NetBuilder()
            .Place("P1", tokens: 1).Place("P2").Place("P3")
            .Transition("T1").Transition("T2").Transition("T3")
            .Arc("P1", "T1").Arc("T1", "P2")
            .Arc("P2", "T2").Arc("T2", "P3")
            .Arc("P3", "T3").Arc("T3", "P1")
            .Build();

        var a = Compute(net);

        Assert.False(a.HasErrors);

        bool hasUniformInvariant = a.PInvariants.Any(inv =>
            inv.Covers("P1") && inv.Covers("P2") && inv.Covers("P3") &&
            inv.Structure["P1"] == inv.Structure["P2"] &&
            inv.Structure["P2"] == inv.Structure["P3"]);
        Assert.True(hasUniformInvariant);
    }
}
