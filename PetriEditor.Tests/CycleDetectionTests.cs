using Analysis;
using Analysis.Engines;
using PetriEditor.Tests.Helpers;

namespace PetriEditor.Tests;

/// <summary>
/// Tests for CyclesAnalysis — verifying that structural cycles through
/// inhibitor and reset arcs are correctly detected (not just normal arcs).
/// </summary>
public class CycleDetectionTests
{
    private static CyclesAnalysis Compute(PetriNetSnapshot net)
    {
        var a = new CyclesAnalysis();
        a.Compute(net);
        return a;
    }

    // ── Normal arcs only (baseline) ───────────────────────────────────────

    [Fact]
    public void SimpleLoop_NormalArcs_CycleDetected()
    {
        var net = new NetBuilder()
            .Place("P1", tokens: 1).Place("P2")
            .Transition("T1").Transition("T2")
            .Arc("P1", "T1").Arc("T1", "P2")
            .Arc("P2", "T2").Arc("T2", "P1")
            .Build();

        var a = Compute(net);

        Assert.False(a.HasErrors);
        Assert.True(a.Cycles.Count > 0);
        bool coversBoth = a.Cycles.Any(c => c.PlaceIds.Contains("P1") && c.PlaceIds.Contains("P2"));
        Assert.True(coversBoth);
    }

    [Fact]
    public void LinearChain_NoNormalCycle_NoCycles()
    {
        // P1->T1->P2->T2->P3 — no cycle
        var net = new NetBuilder()
            .Place("P1", tokens: 1).Place("P2").Place("P3")
            .Transition("T1").Transition("T2")
            .Arc("P1", "T1").Arc("T1", "P2")
            .Arc("P2", "T2").Arc("T2", "P3")
            .Build();

        var a = Compute(net);

        Assert.Empty(a.Cycles);
    }

    // ── Self-loops ────────────────────────────────────────────────────────

    [Fact]
    public void SelfLoop_NormalArc_Detected()
    {
        // P1 --T1--> P1 (self-loop via normal arc)
        var net = new NetBuilder()
            .Place("P1", tokens: 1)
            .Transition("T1")
            .Arc("P1", "T1").Arc("T1", "P1")
            .Build();

        var a = Compute(net);

        Assert.True(a.Cycles.Count > 0);
        bool hasP1InCycle = a.Cycles.Any(c => c.PlaceIds.Contains("P1"));
        Assert.True(hasP1InCycle);
    }

    // ── Inhibitor arcs in cycles ──────────────────────────────────────────

    [Fact]
    public void SelfLoop_InhibitorArc_CycleDetected()
    {
        // P1 --inhibitor--> T1 --normal--> P1
        // Structural cycle P1 -> T1 -> P1 must be detected even though
        // the P1->T1 edge is an inhibitor arc.
        var net = new NetBuilder()
            .Place("P1", tokens: 0)
            .Transition("T1")
            .Inhibitor("P1", "T1")
            .Arc("T1", "P1")
            .Build();

        var a = Compute(net);

        Assert.NotEmpty(a.Cycles); // "Cycle through inhibitor arc was not detected."
        Assert.Contains(a.Cycles, c => c.PlaceIds.Contains("P1"));
    }

    [Fact]
    public void Loop_InhibitorArcAsBackEdge_CycleDetected()
    {
        // P1(1) --normal--> T1 --normal--> P2 --inhibitor--> T2 --normal--> P1
        // Structural cycle P1 -> T1 -> P2 -> T2 -> P1.
        var net = new NetBuilder()
            .Place("P1", tokens: 1).Place("P2")
            .Transition("T1").Transition("T2")
            .Arc("P1", "T1").Arc("T1", "P2")
            .Inhibitor("P2", "T2").Arc("T2", "P1")
            .Build();

        var a = Compute(net);

        Assert.True(a.Cycles.Count > 0, "Cycle with inhibitor back-edge not detected.");
        bool coversBoth = a.Cycles.Any(c => c.PlaceIds.Contains("P1") && c.PlaceIds.Contains("P2"));
        Assert.True(coversBoth);
    }

    [Fact]
    public void Loop_OnlyInhibitorArcs_CycleDetected()
    {
        // P1 --inhibitor--> T1 --inhibitor--> P1 would be an unusual net,
        // but structurally it forms a cycle that should still appear.
        // (An output inhibitor arc is not standard, but our builder allows it.)
        var net = new NetBuilder()
            .Place("P1")
            .Transition("T1")
            .Inhibitor("P1", "T1")
            .Arc("T1", "P1", type: PnArcType.Inhibitor)
            .Build();

        var a = Compute(net);

        Assert.True(a.Cycles.Count > 0);
    }

    // ── Reset arcs in cycles ──────────────────────────────────────────────

    [Fact]
    public void SelfLoop_ResetArc_CycleDetected()
    {
        // P1 --reset--> T1 --normal--> P1
        var net = new NetBuilder()
            .Place("P1", tokens: 2)
            .Transition("T1")
            .Reset("P1", "T1")
            .Arc("T1", "P1")
            .Build();

        var a = Compute(net);

        Assert.NotEmpty(a.Cycles); // "Cycle through reset arc was not detected."
        Assert.Contains(a.Cycles, c => c.PlaceIds.Contains("P1"));
    }

    [Fact]
    public void Loop_ResetArcAsBackEdge_CycleDetected()
    {
        // P1 --normal--> T1 --normal--> P2 --reset--> T2 --normal--> P1
        var net = new NetBuilder()
            .Place("P1", tokens: 1).Place("P2")
            .Transition("T1").Transition("T2")
            .Arc("P1", "T1").Arc("T1", "P2")
            .Reset("P2", "T2").Arc("T2", "P1")
            .Build();

        var a = Compute(net);

        Assert.NotEmpty(a.Cycles);
        Assert.Contains(a.Cycles, c => c.PlaceIds.Contains("P1") && c.PlaceIds.Contains("P2"));
    }

    // ── Mixed arc types in same net ───────────────────────────────────────

    [Fact]
    public void MixedArcs_AllCyclesFound()
    {
        // Two separate cycles: one with normal arcs, one with inhibitor arc.
        // P1 <-normal-> T1 cycle (via P2)
        // P3 <-inhibitor- T2 <-normal- P3 cycle
        var net = new NetBuilder()
            .Place("P1", tokens: 1).Place("P2").Place("P3", tokens: 1)
            .Transition("T1").Transition("T2").Transition("T3")
            .Arc("P1", "T1").Arc("T1", "P2")
            .Arc("P2", "T2").Arc("T2", "P1")
            .Inhibitor("P3", "T3").Arc("T3", "P3")
            .Build();

        var a = Compute(net);

        bool hasNormalCycle   = a.Cycles.Any(c => c.PlaceIds.Contains("P1") && c.PlaceIds.Contains("P2"));
        bool hasInhibitorCycle = a.Cycles.Any(c => c.PlaceIds.Contains("P3"));

        Assert.True(hasNormalCycle,    "Normal arc cycle not detected.");
        Assert.True(hasInhibitorCycle, "Inhibitor arc cycle not detected.");
    }

    // ── Cycle node coverage ───────────────────────────────────────────────

    [Fact]
    public void Cycle_IncludesTransitionIds()
    {
        var net = new NetBuilder()
            .Place("P1", tokens: 1).Place("P2")
            .Transition("T1").Transition("T2")
            .Arc("P1", "T1").Arc("T1", "P2")
            .Arc("P2", "T2").Arc("T2", "P1")
            .Build();

        var a = Compute(net);

        bool hasTransitions = a.Cycles.Any(c => c.TransitionIds.Contains("T1") && c.TransitionIds.Contains("T2"));
        Assert.True(hasTransitions);
    }

    [Fact]
    public void InhibitorCycle_TokensInCycle_CountsPlaceTokens()
    {
        // P1(3) --inhibitor--> T1 --> P1: cycle contains P1 with 3 tokens
        var net = new NetBuilder()
            .Place("P1", tokens: 3)
            .Transition("T1")
            .Inhibitor("P1", "T1")
            .Arc("T1", "P1")
            .Build();

        var a = Compute(net);

        Assert.Contains(a.Cycles, c => c.PlaceIds.Contains("P1") && c.TokensInCycle == 3);
    }

    // ── No duplicate arcs in adjacency ────────────────────────────────────

    [Fact]
    public void DuplicateArcTypes_SameEdge_NoDuplicateCycles()
    {
        // P1 --normal--> T1 AND P1 --inhibitor--> T1 (two arcs, same edge P1->T1)
        // Only one structural edge P1->T1 should appear in adj, producing one cycle.
        var net = new NetBuilder()
            .Place("P1", tokens: 1)
            .Transition("T1")
            .Arc("P1", "T1")
            .Inhibitor("P1", "T1")
            .Arc("T1", "P1")
            .Build();

        var a = Compute(net);

        // There should be exactly one cycle P1->T1->P1, not two
        var p1Cycles = a.Cycles.Where(c => c.PlaceIds.Contains("P1")).ToList();
        Assert.Single(p1Cycles);
    }
}
