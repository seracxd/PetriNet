using Analysis;
using Analysis.Algorithms;
using PetriEditor.Tests.Helpers;

namespace PetriEditor.Tests;

public class ReachabilityTreeTests
{
    private static ReachabilityTreeBuilder Build(PetriNetSnapshot net, int maxNodes = ReachabilityTreeBuilder.MaxNodes)
    {
        var b = new ReachabilityTreeBuilder();
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
    public void SingleTransition_TwoNodes()
    {
        var net = new NetBuilder()
            .Place("P1", tokens: 1).Place("P2")
            .Transition("T1")
            .Arc("P1", "T1").Arc("T1", "P2")
            .Build();

        var b = Build(net);

        Assert.False(b.HasErrors);
        Assert.Equal(2, b.Nodes.Count);
        Assert.Single(b.Edges);
    }

    [Fact]
    public void Root_IsMarkedInitial()
    {
        var net = new NetBuilder()
            .Place("P1", tokens: 1).Place("P2")
            .Transition("T1")
            .Arc("P1", "T1").Arc("T1", "P2")
            .Build();

        var b = Build(net);

        Assert.True(b.Nodes[0].IsInitial);
        Assert.False(b.Nodes[1].IsInitial);
    }

    [Fact]
    public void Deadlock_LabeledOnLeaf()
    {
        var net = new NetBuilder()
            .Place("P1").Place("P2")
            .Transition("T1")
            .Arc("P1", "T1").Arc("T1", "P2")
            .Build();

        var b = Build(net);

        Assert.Single(b.Nodes);
        Assert.True(b.Nodes[0].IsDeadlock);
    }

    // ── Tree vs graph: duplicates are new nodes ───────────────────────────

    [Fact]
    public void Cycle_CreatesNewNodeWithDuplicateFlag()
    {
        // P1(1)->T1->P2, P2->T2->P1
        var net = new NetBuilder()
            .Place("P1", tokens: 1).Place("P2")
            .Transition("T1").Transition("T2")
            .Arc("P1", "T1").Arc("T1", "P2")
            .Arc("P2", "T2").Arc("T2", "P1")
            .Build();

        var b = Build(net);

        // Tree creates a new node even for a revisited marking
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

        // 3 nodes: M0{1,0} -> T1 -> M1{0,1} -> T2 -> M2{1,0}(dup)
        Assert.Equal(3, b.Nodes.Count);
        Assert.True(b.Nodes[2].IsDuplicate);
        // No edges out of the duplicate
        Assert.DoesNotContain(b.Edges, e => e.From == 2);
    }

    [Fact]
    public void DiamondNet_DuplicateAppearsForBothBranches()
    {
        // P1(1) -> T1 -> P2; P1(1) -> T2 -> P2; P2 -> T3 -> P3
        // Both T1 and T2 lead to the same next marking — one is a duplicate
        var net = new NetBuilder()
            .Place("P1", tokens: 1).Place("P2").Place("P3")
            .Transition("T1").Transition("T2").Transition("T3")
            .Arc("P1", "T1").Arc("T1", "P2")
            .Arc("P1", "T2").Arc("T2", "P2")
            .Arc("P2", "T3").Arc("T3", "P3")
            .Build();

        var b = Build(net);

        // Root has 2 children (T1 and T2 result). One of those markings
        // was already seen, so the second occurrence is a duplicate.
        Assert.Contains(b.Nodes, n => n.IsDuplicate);
    }

    // ── Correct markings on nodes ─────────────────────────────────────────

    [Fact]
    public void NodeMarkings_CorrectAfterFiring()
    {
        // P1(2) -> T1 (weight 2) -> P2
        var net = new NetBuilder()
            .Place("P1", tokens: 2).Place("P2")
            .Transition("T1")
            .Arc("P1", "T1", weight: 2).Arc("T1", "P2")
            .Build();

        var b = Build(net);

        Assert.Equal(2, b.Nodes.Count);
        // Root: P1=2, P2=0
        Assert.Equal(2, b.Nodes[0].Marking[0]);
        Assert.Equal(0, b.Nodes[0].Marking[1]);
        // Child: P1=0, P2=1
        Assert.Equal(0, b.Nodes[1].Marking[0]);
        Assert.Equal(1, b.Nodes[1].Marking[1]);
    }

    // ── Edges ─────────────────────────────────────────────────────────────

    [Fact]
    public void Edges_HaveCorrectParentChildRelation()
    {
        var net = new NetBuilder()
            .Place("P1", tokens: 1).Place("P2")
            .Transition("T1")
            .Arc("P1", "T1").Arc("T1", "P2")
            .Build();

        var b = Build(net);

        Assert.Equal(0, b.Edges[0].From);
        Assert.Equal(1, b.Edges[0].To);
    }

    [Fact]
    public void Edges_LabeledWithTransitionName()
    {
        var net = new NetBuilder()
            .Place("P1", tokens: 1).Place("P2")
            .Transition("T1", name: "myT")
            .Arc("P1", "T1").Arc("T1", "P2")
            .Build();

        var b = Build(net);

        Assert.Equal("myT", b.Edges[0].TransitionName);
    }

    // ── Priority ──────────────────────────────────────────────────────────

    [Fact]
    public void Priority_OnlyHighPriorityBranchInTree()
    {
        var net = new NetBuilder()
            .Place("P1", tokens: 1).Place("P2").Place("P3")
            .Transition("T1", priority: 1).Transition("T2", priority: 2)
            .Arc("P1", "T1").Arc("T1", "P2")
            .Arc("P1", "T2").Arc("T2", "P3")
            .Build();

        var b = Build(net);

        // T2 fires only; root has exactly one child
        int childrenOfRoot = b.Edges.Count(e => e.From == 0);
        Assert.Equal(1, childrenOfRoot);
        // That child has P3=1
        var child = b.Nodes[1];
        Assert.Equal(1, child.Marking[2]); // P3
        Assert.Equal(0, child.Marking[1]); // P2 never got a token
    }

    // ── Truncation ────────────────────────────────────────────────────────

    [Fact]
    public void MaxNodes_Truncates()
    {
        var net = new NetBuilder()
            .Place("P1", tokens: 10).Place("P2")
            .Transition("T1")
            .Arc("P1", "T1").Arc("T1", "P2")
            .Build();

        var b = Build(net, maxNodes: 3);

        Assert.True(b.IsTruncated);
        Assert.True(b.Nodes.Count <= 3);
        Assert.True(b.TruncatedIds.Count > 0);
    }

    [Fact]
    public void Cancellation_SetsError()
    {
        var net = new NetBuilder()
            .Place("P1", tokens: 5).Place("P2")
            .Transition("T1")
            .Arc("P1", "T1").Arc("T1", "P2")
            .Build();

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var b = new ReachabilityTreeBuilder();
        b.Build(net, cts.Token);

        Assert.True(b.HasErrors);
    }
}
