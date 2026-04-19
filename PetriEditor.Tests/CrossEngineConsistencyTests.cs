using Analysis;
using Analysis.Algorithms;
using Analysis.Engines;
using PetriEditor.Tests.Helpers;

namespace PetriEditor.Tests;

/// <summary>
/// Cross-engine consistency tests: verifies that the outputs of different analysis
/// engines agree with each other on shared semantic properties.
///
/// Examples: every P-invariant must hold at every reachable marking;
/// the coverability tree must witness the same boundedness verdict as the state space;
/// cancellation must halt expensive computations promptly.
/// </summary>
public class CrossEngineConsistencyTests
{
    // ── P-invariant satisfaction across all reachable markings ───────────────

    /// <summary>
    /// For any ordinary bounded net, every P-invariant found by InvariantAnalysis
    /// must have a constant weighted sum across every state in StateSpaceAnalysis.
    /// This is the fundamental correctness invariant of the Farkas algorithm.
    /// </summary>
    [Fact]
    public void PInvariants_HoldAtEveryReachableMarking_SimpleLoop()
    {
        var net = new NetBuilder()
            .Place("P1", tokens: 2).Place("P2").Place("P3")
            .Transition("T1").Transition("T2").Transition("T3")
            .Arc("P1", "T1").Arc("T1", "P2")
            .Arc("P2", "T2").Arc("T2", "P3")
            .Arc("P3", "T3").Arc("T3", "P1")
            .Build();

        AssertPInvariantsHoldAcrossStateSpace(net);
    }

    [Fact]
    public void PInvariants_HoldAtEveryReachableMarking_MutexNet()
    {
        var net = new NetBuilder()
            .Place("idle1", tokens: 1).Place("crit1")
            .Place("idle2", tokens: 1).Place("crit2")
            .Place("mutex", tokens: 1)
            .Transition("enter1").Transition("exit1")
            .Transition("enter2").Transition("exit2")
            .Arc("idle1", "enter1").Arc("mutex", "enter1").Arc("enter1", "crit1")
            .Arc("crit1", "exit1").Arc("exit1", "idle1").Arc("exit1", "mutex")
            .Arc("idle2", "enter2").Arc("mutex", "enter2").Arc("enter2", "crit2")
            .Arc("crit2", "exit2").Arc("exit2", "idle2").Arc("exit2", "mutex")
            .Build();

        AssertPInvariantsHoldAcrossStateSpace(net);
    }

    [Fact]
    public void PInvariants_HoldAtEveryReachableMarking_WeightedNet()
    {
        // Weighted arc: T1 takes 2 from P1 and produces 2 into P2
        var net = new NetBuilder()
            .Place("P1", tokens: 4).Place("P2")
            .Transition("T1").Transition("T2")
            .Arc("P1", "T1", weight: 2).Arc("T1", "P2", weight: 2)
            .Arc("P2", "T2", weight: 2).Arc("T2", "P1", weight: 2)
            .Build();

        AssertPInvariantsHoldAcrossStateSpace(net);
    }

    private static void AssertPInvariantsHoldAcrossStateSpace(PetriNetSnapshot net)
    {
        var ss  = new StateSpaceAnalysis(); ss.Build(net);
        var inv = new InvariantAnalysis();  inv.Compute(net);

        Assert.False(ss.HasErrors);
        Assert.False(ss.IsTruncated);
        Assert.False(inv.HasErrors);

        var places = net.Places.ToList();
        foreach (var pinv in inv.PInvariants)
        {
            int[] coefs = places.Select(p => pinv.Structure.GetValueOrDefault(p.Id, 0)).ToArray();
            int expected = coefs.Zip(ss.States[0], (c, t) => c * t).Sum();

            foreach (var state in ss.States)
            {
                int actual = coefs.Zip(state, (c, t) => c * t).Sum();
                Assert.Equal(expected, actual);
            }
        }
    }

    // ── Coverability tree agrees with state space on boundedness ─────────────

    [Fact]
    public void CoverabilityTree_AgreesWithStateSpace_BoundedNet()
    {
        var net = new NetBuilder()
            .Place("P1", tokens: 1).Place("P2")
            .Transition("T1").Transition("T2")
            .Arc("P1", "T1").Arc("T1", "P2")
            .Arc("P2", "T2").Arc("T2", "P1")
            .Build();

        var ss = new StateSpaceAnalysis(); ss.Build(net);
        var ct = new CoverabilityTreeBuilder(); ct.Build(net);

        Assert.True(ss.IsBounded());
        // A bounded net's coverability tree contains no omega nodes
        Assert.DoesNotContain(ct.Nodes, n => n.Marking.Any(t => t == CoverabilityTreeBuilder.Omega));
    }

    [Fact]
    public void CoverabilityTree_AgreesWithStateSpace_UnboundedNet()
    {
        // T1 always creates a new token — unbounded
        var net = new NetBuilder()
            .Place("P1", tokens: 1)
            .Transition("T1")
            .Arc("P1", "T1").Arc("T1", "P1").Arc("T1", "P1")
            .Build();

        var ss = new StateSpaceAnalysis(); ss.Build(net, maxStates: 100);
        var ct = new CoverabilityTreeBuilder(); ct.Build(net);

        Assert.True(ss.IsTruncated || !ss.IsBounded());
        Assert.Contains(ct.Nodes, n => n.Marking.Any(t => t == CoverabilityTreeBuilder.Omega));
    }

    [Fact]
    public void CoverabilityTree_MarkingCount_NeverLessThanStateSpace()
    {
        // For bounded nets, coverability tree node count >= state space state count
        // because the tree allows duplicates while the state space deduplicates.
        var net = new NetBuilder()
            .Place("P1", tokens: 1).Place("P2").Place("P3")
            .Transition("T1").Transition("T2").Transition("T3")
            .Arc("P1", "T1").Arc("T1", "P2")
            .Arc("P2", "T2").Arc("T2", "P3")
            .Arc("P3", "T3").Arc("T3", "P1")
            .Build();

        var ss = new StateSpaceAnalysis(); ss.Build(net);
        var ct = new CoverabilityTreeBuilder(); ct.Build(net);

        Assert.True(ct.Nodes.Count >= ss.States.Count);
    }

    // ── Cancellation actually cancels ────────────────────────────────────────

    [Fact]
    public void StateSpaceAnalysis_CancellationToken_StopsEarly()
    {
        // A net that would produce a very large state space if not cancelled
        // (multiple independent tokens circulating)
        var b = new NetBuilder();
        for (int i = 0; i < 6; i++)
        {
            b.Place($"A{i}", tokens: i == 0 ? 1 : 0)
             .Place($"B{i}", tokens: 0)
             .Transition($"T{i}a").Transition($"T{i}b")
             .Arc($"A{i}", $"T{i}a").Arc($"T{i}a", $"B{i}")
             .Arc($"B{i}", $"T{i}b").Arc($"T{i}b", $"A{i}");
        }
        var net = b.Build();

        using var cts = new CancellationTokenSource();
        cts.Cancel();  // cancel immediately

        var ss = new StateSpaceAnalysis();
        ss.Build(net, cts.Token);

        Assert.True(ss.HasErrors);
    }

    [Fact]
    public void TrapCotrapAnalysis_CancellationToken_StopsEarly()
    {
        // Build a net with many places (close to the 20-place limit)
        var b = new NetBuilder();
        for (int i = 0; i < 15; i++)
        {
            b.Place($"P{i}", tokens: i == 0 ? 1 : 0)
             .Transition($"T{i}");
            b.Arc($"P{i}", $"T{i}").Arc($"T{i}", $"P{(i + 1) % 15}");
        }
        var net = b.Build();

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var tc = new TrapCotrapAnalysis();
        Assert.ThrowsAny<OperationCanceledException>(() => tc.Compute(net, cts.Token));
    }

    // ── Trap/siphon semantic consistency ─────────────────────────────────────

    [Fact]
    public void Trap_OnceMarked_RemainsMarked_InStateSpace()
    {
        // A minimal trap: once it has a token, every reachable successor also has a token.
        // Net: P_trap(1) --normal--> T1 --normal--> P_trap (self-loop via T1)
        //      Also T2 adds tokens into P_trap from P_src.
        // This means P_trap is a (trivial) trap — it's its own post-set.
        var net = new NetBuilder()
            .Place("P_trap", tokens: 1)
            .Place("P_src",  tokens: 1)
            .Transition("T_loop").Transition("T_add")
            .Arc("P_trap", "T_loop").Arc("T_loop", "P_trap")
            .Arc("P_src",  "T_add").Arc("T_add", "P_trap")
            .Build();

        var ss   = new StateSpaceAnalysis(); ss.Build(net);
        var trap = new TrapCotrapAnalysis(); trap.Compute(net);

        Assert.False(ss.HasErrors);
        // Cotraps = standard traps: every consuming transition also produces into the set (stays marked)
        Assert.True(trap.Cotraps.Count > 0);

        var places = net.Places.ToList();

        foreach (var trp in trap.Cotraps)
        {
            if (!trp.HasToken) continue; // only marked traps guarantee stay-marked

            var indices = trp.PlaceIds
                .Select(id => places.FindIndex(p => p.Id == id))
                .Where(i => i >= 0)
                .ToHashSet();

            // Every reachable marking must have at least one token somewhere in the trap
            foreach (var state in ss.States)
                Assert.True(indices.Any(i => state[i] > 0),
                    "A marked trap lost all its tokens in a reachable state.");
        }
    }

    // ── FiredTransitions agrees with state space edges ────────────────────────

    [Fact]
    public void FiredTransitions_ContainsExactlyTransitionsOnEdges()
    {
        var net = new NetBuilder()
            .Place("P1", tokens: 1).Place("P2").Place("P3")
            .Transition("T1").Transition("T2").Transition("T3")
            .Arc("P1", "T1").Arc("T1", "P2")
            .Arc("P2", "T2").Arc("T2", "P3")
            .Arc("P3", "T3").Arc("T3", "P1")   // T3 closes the loop
            .Build();

        var ss = new StateSpaceAnalysis(); ss.Build(net);
        var fired = ss.FiredTransitions();

        // Collect edge labels directly from edges
        var edgeLabels = ss.GetEdges()
            .SelectMany(edgeList => edgeList)
            .Select(e => e.TransId)
            .ToHashSet();

        Assert.Equal(edgeLabels.OrderBy(x => x), fired.OrderBy(x => x));
    }

    // ── Cycle deduplication is stable ────────────────────────────────────────

    [Fact]
    public void CycleAnalysis_NoDuplicateCycles_AfterMultipleStartNodes()
    {
        // Johnson's algorithm runs from each start node — rotations of the same cycle
        // must not appear as separate entries.
        var net = new NetBuilder()
            .Place("P1", tokens: 1).Place("P2").Place("P3").Place("P4")
            .Transition("T1").Transition("T2").Transition("T3").Transition("T4")
            .Arc("P1", "T1").Arc("T1", "P2")
            .Arc("P2", "T2").Arc("T2", "P3")
            .Arc("P3", "T3").Arc("T3", "P4")
            .Arc("P4", "T4").Arc("T4", "P1")
            .Build();

        var cyc = new CyclesAnalysis(); cyc.Compute(net);

        // A 4-place ring has exactly 1 elementary cycle
        int p1p2p3p4Cycles = cyc.Cycles.Count(c =>
            c.PlaceIds.Contains("P1") && c.PlaceIds.Contains("P2") &&
            c.PlaceIds.Contains("P3") && c.PlaceIds.Contains("P4"));
        Assert.Equal(1, p1p2p3p4Cycles);
    }

    [Fact]
    public void CycleAnalysis_TwoCycles_BothFound_NoExtras()
    {
        // Two disjoint cycles — exactly 2 cycles expected (no cross-cycle phantoms)
        var net = new NetBuilder()
            .Place("A1", tokens: 1).Place("A2")
            .Place("B1", tokens: 1).Place("B2")
            .Transition("TA1").Transition("TA2")
            .Transition("TB1").Transition("TB2")
            .Arc("A1", "TA1").Arc("TA1", "A2")
            .Arc("A2", "TA2").Arc("TA2", "A1")
            .Arc("B1", "TB1").Arc("TB1", "B2")
            .Arc("B2", "TB2").Arc("TB2", "B1")
            .Build();

        var cyc = new CyclesAnalysis(); cyc.Compute(net);

        Assert.Equal(2, cyc.Cycles.Count);
        Assert.Contains(cyc.Cycles, c => c.PlaceIds.Contains("A1") && c.PlaceIds.Contains("A2"));
        Assert.Contains(cyc.Cycles, c => c.PlaceIds.Contains("B1") && c.PlaceIds.Contains("B2"));
    }

    // ── Reachability tree agrees with state space on marking count ────────────

    [Fact]
    public void ReachabilityTree_IsSupersetOf_StateSpaceMarkings()
    {
        var net = new NetBuilder()
            .Place("P1", tokens: 1).Place("P2")
            .Transition("T1").Transition("T2")
            .Arc("P1", "T1").Arc("T1", "P2")
            .Arc("P2", "T2").Arc("T2", "P1")
            .Build();

        var ss = new StateSpaceAnalysis(); ss.Build(net);
        var rt = new ReachabilityTreeBuilder(); rt.Build(net);

        // Every unique state space marking must appear in at least one tree node
        var treeMarkings = rt.Nodes
            .Select(n => n.Marking)
            .ToList();

        foreach (var state in ss.States)
            Assert.True(treeMarkings.Any(m => m.SequenceEqual(state)),
                $"State [{string.Join(",", state)}] not found in reachability tree.");
    }
}
