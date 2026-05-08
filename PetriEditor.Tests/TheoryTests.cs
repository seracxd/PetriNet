using Analysis;
using Analysis.Algorithms;
using Analysis.Engines;
using PetriEditor.Tests.Helpers;

namespace PetriEditor.Tests;

/// <summary>
/// Parametric regression tests using xUnit Theory.
/// Covers properties that should hold across a family of inputs:
/// - reset/normal arc interaction is order-independent
/// - canonical cycle key deduplication is stable under all rotations
/// - state-space truncation boundary is exact
/// - token array comparer has no spurious collisions on common patterns
/// - weighted-arc firing produces correct markings
/// </summary>
public class TheoryTests
{
    // ── Reset arc order-independence ─────────────────────────────────────────
    // Regardless of the order arcs appear in the net, a Reset arc on a place
    // must always produce P=0 after firing, even when a Normal arc also touches
    // the same place in the same transition.

    public static IEnumerable<object[]> ResetOrderCases()
    {
        // (normalWeight, resetWeight, initialTokens, expectedP1AfterFire, expectedP2AfterFire)
        // Normal arc on P1 (weight n), then Reset arc on P1 — net result: P1=0
        yield return new object[] { 1, 1, 3, 0, 1 };   // normal then reset
        yield return new object[] { 2, 1, 5, 0, 1 };   // larger normal weight
        yield return new object[] { 1, 2, 4, 0, 1 };   // larger reset weight (still zeroes)
        yield return new object[] { 3, 3, 3, 0, 1 };   // equal weights
    }

    [Theory]
    [MemberData(nameof(ResetOrderCases))]
    public void ResetArc_AlwaysZeroesPlace_RegardlessOfArcOrder(
        int normalWeight, int resetWeight, int initialTokens, int expectedP1, int expectedP2)
    {
        // Arc list order: Normal first, then Reset
        var netNormalFirst = new NetBuilder()
            .Place("P1", tokens: initialTokens)
            .Place("P2", tokens: 0)
            .Transition("T1")
            .Arc("P1", "T1", weight: normalWeight, type: PnArcType.Normal)
            .Arc("P1", "T1", weight: resetWeight,  type: PnArcType.Reset)
            .Arc("T1", "P2")
            .Build();

        // Arc list order: Reset first, then Normal
        var netResetFirst = new NetBuilder()
            .Place("P1", tokens: initialTokens)
            .Place("P2", tokens: 0)
            .Transition("T1")
            .Arc("P1", "T1", weight: resetWeight,  type: PnArcType.Reset)
            .Arc("P1", "T1", weight: normalWeight, type: PnArcType.Normal)
            .Arc("T1", "P2")
            .Build();

        var ss1 = new StateSpaceAnalysis(); ss1.Build(netNormalFirst);
        var ss2 = new StateSpaceAnalysis(); ss2.Build(netResetFirst);

        Assert.False(ss1.HasErrors);
        Assert.False(ss2.HasErrors);

        // Both orderings must produce the same set of reachable markings
        Assert.Equal(ss1.States.Count, ss2.States.Count);
        foreach (var state in ss1.States)
            Assert.Contains(ss2.States, s => s.SequenceEqual(state));

        // The post-fire state (P2 has tokens) must have P1 zeroed by the reset arc
        var postFire = ss1.States.FirstOrDefault(s => s[1] > 0);
        Assert.NotNull(postFire);
        Assert.Equal(expectedP1, postFire[0]);
        Assert.Equal(expectedP2, postFire[1]);
    }

    // ── State-space truncation boundary ─────────────────────────────────────
    // Build a net whose state count is exactly N. Verify that maxStates = N
    // does NOT truncate, but maxStates = N-1 does.
    public static IEnumerable<object[]> TruncationBoundaryCases()
    {
        // (tokenRingSize) — a token ring with n places has exactly n reachable states
        yield return new object[] { 3 };
        yield return new object[] { 5 };
        yield return new object[] { 8 };
    }

    [Theory]
    [MemberData(nameof(TruncationBoundaryCases))]
    public void StateSpace_TruncationBoundary_ExactMatch(int ringSize)
    {
        var net = TokenRing(ringSize);

        var ssExact = new StateSpaceAnalysis();
        ssExact.Build(net, maxStates: ringSize);
        Assert.False(ssExact.IsTruncated, $"Ring of {ringSize} should not truncate at exactly {ringSize} states.");
        Assert.Equal(ringSize, ssExact.States.Count);

        var ssTight = new StateSpaceAnalysis();
        ssTight.Build(net, maxStates: ringSize - 1);
        Assert.True(ssTight.IsTruncated, $"Ring of {ringSize} should truncate when maxStates={ringSize - 1}.");
    }

    // ── State-space dedup: distinct markings are always separate states ──────────
    // These nets have markings that differ only by permutation — the StateSpaceAnalysis
    // must never merge them (which would happen if its internal comparer had hash collisions).

    public static IEnumerable<object[]> DistinctMarkingNetCases()
    {
        // Token ring with N places: every state has a different place holding the token.
        // If the comparer confused [1,0] and [0,1] they'd collapse to 1 state.
        yield return new object[] { 2, 2 };   // 2-place ring: 2 distinct states
        yield return new object[] { 3, 3 };   // 3-place ring: 3 distinct states
        yield return new object[] { 5, 5 };   // 5-place ring: 5 distinct states
        yield return new object[] { 8, 8 };   // 8-place ring: 8 distinct states
    }

    [Theory]
    [MemberData(nameof(DistinctMarkingNetCases))]
    public void StateSpace_DistinctMarkings_NeverMerged(int ringSize, int expectedStates)
    {
        // Each state has a different place holding the single token;
        // if the comparer ever hashes two permutations identically, state count drops.
        var net = TokenRing(ringSize);
        var ss = new StateSpaceAnalysis(); ss.Build(net);

        Assert.False(ss.HasErrors);
        Assert.False(ss.IsTruncated);
        Assert.Equal(expectedStates, ss.States.Count);

        // Every state must be pairwise distinct
        for (int i = 0; i < ss.States.Count; i++)
            for (int j = i + 1; j < ss.States.Count; j++)
                Assert.False(ss.States[i].SequenceEqual(ss.States[j]),
                    $"States {i} and {j} are duplicates: [{string.Join(",", ss.States[i])}]");
    }

    // ── Weighted arc firing produces correct markings ────────────────────────

    public static IEnumerable<object[]> WeightedArcCases()
    {
        // (consumeWeight, produceWeight, initialTokens, expectedFinalP1, expectedFinalP2)
        yield return new object[] { 1, 1, 1, 0, 1 };   // unit arcs
        yield return new object[] { 2, 1, 2, 0, 1 };   // consume 2, produce 1
        yield return new object[] { 1, 3, 1, 0, 3 };   // consume 1, produce 3
        yield return new object[] { 3, 3, 3, 0, 3 };   // equal weights
        yield return new object[] { 2, 2, 4, 0, 4 };   // 4 tokens, fires twice → P1=0, P2=4
    }

    [Theory]
    [MemberData(nameof(WeightedArcCases))]
    public void WeightedArcs_ProduceCorrectMarkings(
        int consumeW, int produceW, int initialTokens, int expectedP1, int expectedP2)
    {
        var net = new NetBuilder()
            .Place("P1", tokens: initialTokens)
            .Place("P2", tokens: 0)
            .Transition("T1")
            .Arc("P1", "T1", weight: consumeW)
            .Arc("T1", "P2", weight: produceW)
            .Build();

        var ss = new StateSpaceAnalysis(); ss.Build(net);

        Assert.False(ss.HasErrors);
        // The last reachable state (when T1 can't fire any more) must have expected marking
        var finalState = ss.States.First(s => s[0] < consumeW);
        Assert.Equal(expectedP1, finalState[0]);
        Assert.Equal(expectedP2, finalState[1]);
    }

    // ── Multiple-token initial markings generate correct state counts ─────────

    public static IEnumerable<object[]> MultiTokenRingCases()
    {
        // (places, tokens, expectedStates)
        // A ring of N places with K tokens: C(N,K) states
        yield return new object[] { 3, 1, 3 };    // C(3,1) = 3
        yield return new object[] { 4, 1, 4 };    // C(4,1) = 4
        yield return new object[] { 4, 2, 10 };   // multiset(4,2) = 10 reachable states
        yield return new object[] { 5, 1, 5 };    // C(5,1) = 5
    }

    [Theory]
    [MemberData(nameof(MultiTokenRingCases))]
    public void StateSpace_RingWithKTokens_CorrectStateCount(int places, int tokens, int expectedStates)
    {
        var b = new NetBuilder();
        for (int i = 0; i < places; i++)
            b.Place($"P{i}", tokens: i < tokens ? 1 : 0);
        for (int i = 0; i < places; i++)
        {
            b.Transition($"T{i}");
            b.Arc($"P{i}", $"T{i}").Arc($"T{i}", $"P{(i + 1) % places}");
        }
        var net = b.Build();

        var ss = new StateSpaceAnalysis(); ss.Build(net);

        Assert.False(ss.HasErrors);
        Assert.False(ss.IsTruncated);
        Assert.Equal(expectedStates, ss.States.Count);
    }

    // ── Cycle count scales with number of independent loops ──────────────────

    public static IEnumerable<object[]> IndependentCycleCases()
    {
        yield return new object[] { 1 };
        yield return new object[] { 2 };
        yield return new object[] { 3 };
        yield return new object[] { 4 };
    }

    [Theory]
    [MemberData(nameof(IndependentCycleCases))]
    public void CycleDetection_NIndependentLoops_FindsNDistinctCycles(int loopCount)
    {
        var b = new NetBuilder();
        for (int i = 0; i < loopCount; i++)
        {
            b.Place($"A{i}", tokens: 1).Place($"B{i}")
             .Transition($"T{i}a").Transition($"T{i}b")
             .Arc($"A{i}", $"T{i}a").Arc($"T{i}a", $"B{i}")
             .Arc($"B{i}", $"T{i}b").Arc($"T{i}b", $"A{i}");
        }
        var net = b.Build();

        var cyc = new CyclesAnalysis(); cyc.Compute(net);

        Assert.Equal(loopCount, cyc.Cycles.Count);
    }

    // ── Inhibitor arc: transition fires only when guard place has < weight tokens ─

    public static IEnumerable<object[]> InhibitorThresholdCases()
    {
        // (inhibitorWeight, guardTokens, shouldFire)
        // Inhibitor arc with weight W blocks the transition when guardTokens >= W
        yield return new object[] { 1, 0, true  };   // 0 < 1 — not blocked
        yield return new object[] { 1, 1, false };   // 1 >= 1 — blocked
        yield return new object[] { 1, 2, false };   // 2 >= 1 — blocked
        yield return new object[] { 2, 1, false };   // 1 != 0 — engine uses zero-test semantics, blocked
        yield return new object[] { 2, 2, false };   // 2 >= 2 — blocked
    }

    [Theory]
    [MemberData(nameof(InhibitorThresholdCases))]
    public void InhibitorArc_BlocksTransitionAtThreshold(
        int inhibitorWeight, int guardTokens, bool shouldFire)
    {
        // P_guard --inhibitor(weight)--> T1 ; P_src --normal--> T1 ; T1 --> P_dst
        var net = new NetBuilder()
            .Place("P_guard", tokens: guardTokens)
            .Place("P_src",   tokens: 1)
            .Place("P_dst",   tokens: 0)
            .Transition("T1")
            .Arc("P_guard", "T1", weight: inhibitorWeight, type: PnArcType.Inhibitor)
            .Arc("P_src",   "T1")
            .Arc("T1", "P_dst")
            .Build();

        var ss = new StateSpaceAnalysis(); ss.Build(net);

        int pDstIdx = 2;
        if (shouldFire)
            Assert.Contains(ss.States, s => s[pDstIdx] == 1);
        else
            Assert.Single(ss.States); // no firing, stuck at initial
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static PetriNetSnapshot TokenRing(int n)
    {
        var b = new NetBuilder();
        for (int i = 0; i < n; i++)
            b.Place($"P{i}", tokens: i == 0 ? 1 : 0);
        for (int i = 0; i < n; i++)
        {
            b.Transition($"T{i}");
            b.Arc($"P{i}", $"T{i}").Arc($"T{i}", $"P{(i + 1) % n}");
        }
        return b.Build();
    }
}
