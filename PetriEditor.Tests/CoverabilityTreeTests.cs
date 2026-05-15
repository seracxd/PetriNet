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

        var b = new CoverabilityTreeBuilder();
        b.Build(net, disableOmegaAcceleration: true);

        Assert.False(b.HasErrors);
        // With acceleration disabled (correct mode for inhibitor nets), no omega
        // should ever appear in the tree.
        Assert.DoesNotContain(b.Nodes, n => n.Marking.Contains(CoverabilityTreeBuilder.Omega));
    }

    [Fact]
    public void InhibitorArc_EnabledWhenGuardEmpty_FiresOnce()
    {
        // P1 inhibits T1; P_src(1) → T1 → P_dst.
        // Guard P1 is empty, T1 fires once and net deadlocks.
        var net = new NetBuilder()
            .Place("P1").Place("P_src", tokens: 1).Place("P_dst")
            .Transition("T1")
            .Inhibitor("P1", "T1")
            .Arc("P_src", "T1").Arc("T1", "P_dst")
            .Build();

        var b = new CoverabilityTreeBuilder();
        b.Build(net, disableOmegaAcceleration: true);

        Assert.False(b.HasErrors);
        Assert.Equal(2, b.Nodes.Count);             // root + one child
        Assert.True(b.Nodes[1].IsDeadlock);          // child is dead (no inputs)
        // Final marking: P1=0, P_src=0, P_dst=1
        Assert.Equal(new[] { 0, 0, 1 }, b.Nodes[1].Marking);
    }

    [Fact]
    public void InhibitorArc_BlocksFiringWhenGuardAtThreshold()
    {
        // Weighted inhibitor: P1 has 2 tokens, inhibitor weight=2 → T1 blocked.
        // Net has no other enabled transitions → root is a deadlock.
        var net = new NetBuilder()
            .Place("P1", tokens: 2).Place("P_src", tokens: 1).Place("P_dst")
            .Transition("T1")
            .Arc("P1", "T1", weight: 2, type: PnArcType.Inhibitor)
            .Arc("P_src", "T1").Arc("T1", "P_dst")
            .Build();

        var b = new CoverabilityTreeBuilder();
        b.Build(net, disableOmegaAcceleration: true);

        Assert.False(b.HasErrors);
        Assert.Single(b.Nodes);
        Assert.True(b.Nodes[0].IsDeadlock);
    }

    [Fact]
    public void InhibitorArc_WeightedAllowsFireBelowThreshold()
    {
        // Weighted inhibitor: P1 has 1 token, inhibitor weight=2 → 1 < 2, T1 fires.
        var net = new NetBuilder()
            .Place("P1", tokens: 1).Place("P_src", tokens: 1).Place("P_dst")
            .Transition("T1")
            .Arc("P1", "T1", weight: 2, type: PnArcType.Inhibitor)
            .Arc("P_src", "T1").Arc("T1", "P_dst")
            .Build();

        var b = new CoverabilityTreeBuilder();
        b.Build(net, disableOmegaAcceleration: true);

        Assert.False(b.HasErrors);
        Assert.Equal(2, b.Nodes.Count);
        // Inhibitor consumes nothing → P1 unchanged at 1
        Assert.Equal(new[] { 1, 0, 1 }, b.Nodes[1].Marking);
    }

    // ── Reset arc semantics ───────────────────────────────────────────────

    [Fact]
    public void ResetArc_DoesNotGuardEnablement_FiresFromZero()
    {
        // Reset-only input on P1=0: T1 still fires (Aalst Def. 2).
        // Output P2 receives a token. Without a normal input, T1 fires forever,
        // but acceleration is off so we cap with MaxNodes.
        var net = new NetBuilder()
            .Place("P1").Place("P2")
            .Transition("T1")
            .Reset("P1", "T1").Arc("T1", "P2")
            .Build();

        var b = new CoverabilityTreeBuilder();
        b.Build(net, maxNodes: 10, disableOmegaAcceleration: true);

        // Root marking [0,0] is not a deadlock — T1 fires.
        Assert.False(b.Nodes[0].IsDeadlock);
        // Child after T1: P1=0 (reset), P2=1.
        Assert.Contains(b.Nodes, n => n.Marking.Length == 2 && n.Marking[0] == 0 && n.Marking[1] == 1);
    }

    [Fact]
    public void ResetArc_ClearsPlaceOnFire_OmegaModeIntroducesOmegaOnOutput()
    {
        // P1(3) --reset--> T1 ; T1 --> P2.
        // With omega acceleration ON (treating it as a "normal" net for testing
        // monotonicity behavior): firing T1 strictly grows P2, so the ancestor
        // chain [3,0] → [0,1] triggers neither (not dominated, P1 decreased).
        // But after another fire [0,1] → [0,2] dominates [0,1] → P2 becomes ω.
        var net = new NetBuilder()
            .Place("P1", tokens: 3).Place("P2")
            .Transition("T1")
            .Reset("P1", "T1").Arc("T1", "P2")
            .Build();

        var b = new CoverabilityTreeBuilder();
        b.Build(net, disableOmegaAcceleration: false);

        Assert.False(b.HasErrors);
        // P2 column must contain ω somewhere (unbounded under reset+production).
        Assert.Contains(b.Nodes, n => n.Marking[1] == CoverabilityTreeBuilder.Omega);
        // P1 column never carries ω — reset clamps it to 0.
        Assert.DoesNotContain(b.Nodes, n => n.Marking[0] == CoverabilityTreeBuilder.Omega);
    }

    [Fact]
    public void ResetArc_PostFireMarkingHasZeroAtResetPlace()
    {
        // P1(5) with reset+normal input arcs, P2 output.
        // Whatever the normal arc subtracts gets overwritten by reset to 0.
        var net = new NetBuilder()
            .Place("P1", tokens: 5).Place("P2")
            .Transition("T1")
            .Arc("P1", "T1")           // normal, weight 1
            .Reset("P1", "T1")          // reset on same place
            .Arc("T1", "P2")
            .Build();

        var b = new CoverabilityTreeBuilder();
        b.Build(net, maxNodes: 10, disableOmegaAcceleration: true);

        // Every reachable post-fire marking must have P1=0.
        var postFire = b.Nodes.Where(n => !n.IsInitial).ToList();
        Assert.NotEmpty(postFire);
        Assert.All(postFire, n => Assert.Equal(0, n.Marking[0]));
    }

    [Fact]
    public void ResetArc_WeightedDoesNotGuard_SameAsUnweighted()
    {
        // Reset arc weight is irrelevant for enablement (Aalst Def. 2).
        // P1=0 with reset weight=5 still fires.
        var net = new NetBuilder()
            .Place("P1").Place("P2")
            .Transition("T1")
            .Arc("P1", "T1", weight: 5, type: PnArcType.Reset)
            .Arc("T1", "P2")
            .Build();

        var b = new CoverabilityTreeBuilder();
        b.Build(net, maxNodes: 5, disableOmegaAcceleration: true);

        Assert.False(b.Nodes[0].IsDeadlock);
        Assert.Contains(b.Nodes, n => n.Marking[1] >= 1);
    }

    // ── Mixed semantics ───────────────────────────────────────────────────

    [Fact]
    public void InhibitorAndReset_OnDifferentPlaces_BothApplied()
    {
        // P_inh inhibits T1, P_rst is reset by T1, P_src normal-feeds T1, P_dst output.
        // Initial: P_inh=0, P_rst=4, P_src=1, P_dst=0.
        var net = new NetBuilder()
            .Place("P_inh").Place("P_rst", tokens: 4)
            .Place("P_src", tokens: 1).Place("P_dst")
            .Transition("T1")
            .Inhibitor("P_inh", "T1")
            .Reset("P_rst", "T1")
            .Arc("P_src", "T1").Arc("T1", "P_dst")
            .Build();

        var b = new CoverabilityTreeBuilder();
        b.Build(net, disableOmegaAcceleration: true);

        Assert.False(b.HasErrors);
        Assert.Equal(2, b.Nodes.Count);
        // After T1: P_inh=0 (untouched), P_rst=0 (reset), P_src=0 (consumed), P_dst=1.
        Assert.Equal(new[] { 0, 0, 0, 1 }, b.Nodes[1].Marking);
        Assert.True(b.Nodes[1].IsDeadlock);
    }
}
