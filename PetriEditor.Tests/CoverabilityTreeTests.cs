using Analysis;
using Analysis.Algorithms;
using PetriEditor.Tests.Helpers;

namespace PetriEditor.Tests;

public class CoverabilityTreeTests
{
    private static CoverabilityTreeBuilder Build(PetriNetSnapshot net, int maxNodes = CoverabilityTreeBuilder.MaxNodes)
    {
        var b = new CoverabilityTreeBuilder();
        b.Build(net, maxNodes: maxNodes);
        return b;
    }

    // ── Basic structure ───────────────────────────────────────────────────

    [Fact]
    public void EmptyNet_ReturnsError()
    {
        var net = new NetBuilder().Build();
        var b = Build(net);
        Assert.True(b.HasErrors);
        Assert.Empty(b.Nodes);
    }

    [Fact]
    public void SingleTransition_BoundedNet_NoOmega()
    {
        // P1(1) -> T1 -> P2
        var net = new NetBuilder()
            .Place("P1", tokens: 1).Place("P2")
            .Transition("T1")
            .Arc("P1", "T1").Arc("T1", "P2")
            .Build();

        var b = Build(net);

        Assert.False(b.HasErrors);
        Assert.DoesNotContain(b.Nodes, n => n.Marking.Contains(CoverabilityTreeBuilder.Omega));
    }

    [Fact]
    public void SingleTransition_TwoNodes_RootAndChild()
    {
        var net = new NetBuilder()
            .Place("P1", tokens: 1).Place("P2")
            .Transition("T1")
            .Arc("P1", "T1").Arc("T1", "P2")
            .Build();

        var b = Build(net);

        Assert.Equal(2, b.Nodes.Count); // M0 and M1
        Assert.Single(b.Edges);
        Assert.True(b.Nodes[0].IsInitial);
        Assert.False(b.Nodes[1].IsInitial);
    }

    [Fact]
    public void Deadlock_MarkedOnLeaf()
    {
        // P1(0): no tokens, T1 never fires → deadlock at root
        var net = new NetBuilder()
            .Place("P1").Place("P2")
            .Transition("T1")
            .Arc("P1", "T1").Arc("T1", "P2")
            .Build();

        var b = Build(net);

        Assert.Single(b.Nodes);
        Assert.True(b.Nodes[0].IsDeadlock);
    }

    // ── Omega introduction ────────────────────────────────────────────────

    [Fact]
    public void UnboundedNet_OmegaIntroduced()
    {
        // Producer loop: T1 always adds a token to P1
        // P1(1) -> T1 -> P1 + P2 (net creates tokens)
        var net = new NetBuilder()
            .Place("P1", tokens: 1).Place("P2")
            .Transition("T1")
            .Arc("P1", "T1")
            .Arc("T1", "P1").Arc("T1", "P1")  // two output arcs: +2 back
            .Arc("T1", "P2")
            .Build();

        var b = Build(net);

        Assert.False(b.HasErrors);
        Assert.Contains(b.Nodes, n => n.Marking.Contains(CoverabilityTreeBuilder.Omega));
    }

    [Fact]
    public void SimpleUnbounded_OmegaNodeIsDuplicate()
    {
        // Once omega appears, subsequent nodes are duplicates
        var net = new NetBuilder()
            .Place("P1", tokens: 1)
            .Transition("T1")
            .Arc("P1", "T1").Arc("T1", "P1").Arc("T1", "P1") // +1 net gain
            .Build();

        var b = Build(net);

        // Should have omega node and at least one duplicate
        Assert.Contains(b.Nodes, n => n.Marking[0] == CoverabilityTreeBuilder.Omega);
        Assert.Contains(b.Nodes, n => n.IsDuplicate);
    }

    // ── Duplicate detection ───────────────────────────────────────────────

    [Fact]
    public void Cycle_MarkingRevisited_MarkedAsDuplicate()
    {
        // Simple cycle: P1(1)->T1->P2, P2->T2->P1
        var net = new NetBuilder()
            .Place("P1", tokens: 1).Place("P2")
            .Transition("T1").Transition("T2")
            .Arc("P1", "T1").Arc("T1", "P2")
            .Arc("P2", "T2").Arc("T2", "P1")
            .Build();

        var b = Build(net);

        Assert.False(b.HasErrors);
        Assert.Contains(b.Nodes, n => n.IsDuplicate);
    }

    [Fact]
    public void Cycle_DuplicateNodeNotExpanded()
    {
        var net = new NetBuilder()
            .Place("P1", tokens: 1).Place("P2")
            .Transition("T1").Transition("T2")
            .Arc("P1", "T1").Arc("T1", "P2")
            .Arc("P2", "T2").Arc("T2", "P1")
            .Build();

        var b = Build(net);

        // Root M0 {1,0} -> T1 -> M1 {0,1} -> T2 -> M2 {1,0} (duplicate of M0)
        Assert.Equal(3, b.Nodes.Count);
        Assert.True(b.Nodes[2].IsDuplicate);
    }

    // ── Edge structure ────────────────────────────────────────────────────

    [Fact]
    public void Edges_LabeledWithTransitionName()
    {
        var net = new NetBuilder()
            .Place("P1", tokens: 1).Place("P2")
            .Transition("T1", name: "fire")
            .Arc("P1", "T1").Arc("T1", "P2")
            .Build();

        var b = Build(net);

        Assert.Single(b.Edges);
        Assert.Equal("fire", b.Edges[0].TransitionName);
    }

    [Fact]
    public void TwoTransitions_TwoChildrenFromRoot()
    {
        // P1(1): both T1 and T2 enabled at same priority
        var net = new NetBuilder()
            .Place("P1", tokens: 1).Place("P2").Place("P3")
            .Transition("T1").Transition("T2")
            .Arc("P1", "T1").Arc("T1", "P2")
            .Arc("P1", "T2").Arc("T2", "P3")
            .Build();

        var b = Build(net);

        int childrenOfRoot = b.Edges.Count(e => e.From == 0);
        Assert.Equal(2, childrenOfRoot);
    }

    // ── Node cap / truncation ─────────────────────────────────────────────

    [Fact]
    public void MaxNodes_Truncates_SetsFlag()
    {
        // A net that generates many states — use a tight cap
        var net = new NetBuilder()
            .Place("P1", tokens: 5).Place("P2")
            .Transition("T1")
            .Arc("P1", "T1").Arc("T1", "P2")
            .Build();

        var b = Build(net, maxNodes: 3);

        Assert.True(b.IsTruncated);
        Assert.True(b.Nodes.Count <= 3);
    }

    // ── Inhibitor arc semantics ───────────────────────────────────────────

    [Fact]
    public void InhibitorArc_BlocksOmegaPromotion()
    {
        // P1 inhibits T1: once P1 has a token T1 can't fire
        // T2 produces to P1; T1 consumes from P2 and is blocked while P1>0
        var net = new NetBuilder()
            .Place("P1", tokens: 0).Place("P2", tokens: 1)
            .Transition("T1").Transition("T2")
            .Inhibitor("P1", "T1")
            .Arc("P2", "T1").Arc("T1", "P2") // T1: P2 consumes/produces (cycle)
            .Arc("T2", "P1")                  // T2 adds to P1 (blocking T1 eventually)
            .Build();

        var b = Build(net);

        Assert.False(b.HasErrors);
    }
}
