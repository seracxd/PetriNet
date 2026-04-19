using Analysis;
using Analysis.Algorithms;
using Analysis.Engines;
using PetriEditor.Tests.Helpers;

namespace PetriEditor.Tests;

/// <summary>
/// Integration-style tests against classical Petri net patterns from theory.
/// Each test builds all engine results and asserts multiple known properties.
/// These are significantly more complex than unit tests — they validate
/// cross-engine consistency on non-trivial nets with well-understood behaviour.
/// </summary>
public class ClassicalNetsTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private static (StateSpaceAnalysis ss, CoverabilityTreeBuilder ct,
                    InvariantAnalysis inv, TrapCotrapAnalysis trap,
                    CyclesAnalysis cyc) Analyse(PetriNetSnapshot net, int maxStates = 50_000)
    {
        var ss  = new StateSpaceAnalysis(); ss.Build(net, maxStates: maxStates);
        var ct  = new CoverabilityTreeBuilder(); ct.Build(net);
        var inv = new InvariantAnalysis();  inv.Compute(net);
        var trap = new TrapCotrapAnalysis(); trap.Compute(net);
        var cyc  = new CyclesAnalysis();    cyc.Compute(net);
        return (ss, ct, inv, trap, cyc);
    }

    // ── Producer-Consumer ────────────────────────────────────────────────────
    // Classic 1-capacity buffer. Token circulates: ready → produce → full → consume → ready.
    // Expected: safe, bounded, live, reversible, deadlock-free, conservative.

    private static PetriNetSnapshot ProducerConsumer() => new NetBuilder()
        .Place("ready",  tokens: 1)
        .Place("full",   tokens: 0)
        .Transition("produce")
        .Transition("consume")
        .Arc("ready", "produce").Arc("produce", "full")
        .Arc("full",  "consume").Arc("consume", "ready")
        .Build();

    [Fact]
    public void ProducerConsumer_IsLiveAndSafe()
    {
        var net = ProducerConsumer();
        var (ss, _, _, _, _) = Analyse(net);

        Assert.False(ss.HasErrors);
        Assert.False(ss.IsTruncated);
        Assert.True(ss.IsBounded());
        Assert.True(ss.IsDeadlockFree());
        Assert.True(ss.IsReversible());
        Assert.True(ss.IsSafe());
        Assert.Equal(2, ss.States.Count);  // {1,0} and {0,1}
    }

    [Fact]
    public void ProducerConsumer_PInvariantConservesTokens()
    {
        var net = ProducerConsumer();
        var (ss, _, inv, _, _) = Analyse(net);

        Assert.False(inv.HasErrors);
        Assert.True(inv.PInvariants.Count > 0);

        // Every reachable marking satisfies every P-invariant
        var places = net.Places.ToList();
        foreach (var pinv in inv.PInvariants)
        {
            int[] coefs = places.Select(p => pinv.Structure.GetValueOrDefault(p.Id, 0)).ToArray();
            int expectedSum = coefs.Zip(ss.States[0], (c, t) => c * t).Sum();

            foreach (var state in ss.States)
            {
                int actualSum = coefs.Zip(state, (c, t) => c * t).Sum();
                Assert.Equal(expectedSum, actualSum);
            }
        }
    }

    [Fact]
    public void ProducerConsumer_HasCycleAndTrap()
    {
        var net = ProducerConsumer();
        var (_, _, _, trap, cyc) = Analyse(net);

        Assert.True(cyc.Cycles.Count > 0);
        Assert.True(trap.Traps.Count > 0);
        Assert.True(trap.Cotraps.Count > 0);
    }

    // ── Mutual Exclusion ─────────────────────────────────────────────────────
    // Two processes compete for a shared mutex token. At most one can be in
    // its critical section simultaneously.
    // Expected: bounded, deadlock-free, reversible, NOT safe (P_mutex starts with 1 but
    // places like P_idle1 and P_wait1 can each hold 1 — but never crit1 and crit2 together).

    private static PetriNetSnapshot MutualExclusion() => new NetBuilder()
        .Place("idle1",  tokens: 1)
        .Place("wait1",  tokens: 0)
        .Place("crit1",  tokens: 0)
        .Place("idle2",  tokens: 1)
        .Place("wait2",  tokens: 0)
        .Place("crit2",  tokens: 0)
        .Place("mutex",  tokens: 1)
        .Transition("req1").Transition("enter1").Transition("exit1")
        .Transition("req2").Transition("enter2").Transition("exit2")
        // Process 1
        .Arc("idle1", "req1").Arc("req1", "wait1")
        .Arc("wait1", "enter1").Arc("mutex", "enter1")
        .Arc("enter1", "crit1")
        .Arc("crit1", "exit1").Arc("exit1", "idle1").Arc("exit1", "mutex")
        // Process 2
        .Arc("idle2", "req2").Arc("req2", "wait2")
        .Arc("wait2", "enter2").Arc("mutex", "enter2")
        .Arc("enter2", "crit2")
        .Arc("crit2", "exit2").Arc("exit2", "idle2").Arc("exit2", "mutex")
        .Build();

    [Fact]
    public void MutualExclusion_BoundedDeadlockFreeReversible()
    {
        var net = MutualExclusion();
        var (ss, _, _, _, _) = Analyse(net);

        Assert.False(ss.HasErrors);
        Assert.False(ss.IsTruncated);
        Assert.True(ss.IsBounded());
        Assert.True(ss.IsDeadlockFree());
        Assert.True(ss.IsReversible());
    }

    [Fact]
    public void MutualExclusion_NeverBothInCriticalSection()
    {
        var net = MutualExclusion();
        var (ss, _, _, _, _) = Analyse(net);

        var places = net.Places.ToList();
        int crit1Idx = places.FindIndex(p => p.Id == "crit1");
        int crit2Idx = places.FindIndex(p => p.Id == "crit2");

        // Mutual exclusion: crit1 and crit2 can never both be 1
        Assert.DoesNotContain(ss.States, s => s[crit1Idx] == 1 && s[crit2Idx] == 1);
    }

    [Fact]
    public void MutualExclusion_MutexInvariantSumsToOne()
    {
        // The mutex + crit1 + crit2 sum must always equal 1 (the mutex token)
        var net = MutualExclusion();
        var (ss, _, inv, _, _) = Analyse(net);

        var places = net.Places.ToList();
        int mutexIdx = places.FindIndex(p => p.Id == "mutex");
        int crit1Idx = places.FindIndex(p => p.Id == "crit1");
        int crit2Idx = places.FindIndex(p => p.Id == "crit2");

        foreach (var state in ss.States)
            Assert.Equal(1, state[mutexIdx] + state[crit1Idx] + state[crit2Idx]);

        // P-invariant should cover mutex, crit1, crit2
        Assert.Contains(inv.PInvariants, pi =>
            pi.Covers("mutex") && pi.Covers("crit1") && pi.Covers("crit2"));
    }

    // ── Dining Philosophers (3, non-atomic two-step pickup — deadlock-prone) ──
    // Each philosopher picks up their LEFT fork first, then their RIGHT fork.
    // This creates a circular wait: all three can simultaneously hold one fork
    // and be blocked waiting for the next → deadlock.
    // Expected: bounded, NOT deadlock-free, NOT live, NOT reversible.

    private static PetriNetSnapshot DiningPhilosophers3NonAtomic() => new NetBuilder()
        // Thinking / partial (holds left fork) / eating places
        .Place("think0", tokens: 1).Place("partial0").Place("eat0")
        .Place("think1", tokens: 1).Place("partial1").Place("eat1")
        .Place("think2", tokens: 1).Place("partial2").Place("eat2")
        // Three forks in a ring: fork_A shared by Phil0(left)/Phil2(right),
        //                         fork_B shared by Phil1(left)/Phil0(right),
        //                         fork_C shared by Phil2(left)/Phil1(right)
        .Place("fork_A", tokens: 1).Place("fork_B", tokens: 1).Place("fork_C", tokens: 1)
        // Phil0: grabs fork_A (left), then fork_B (right)
        .Transition("gl0").Transition("gr0").Transition("rel0")
        .Arc("think0", "gl0").Arc("fork_A", "gl0").Arc("gl0", "partial0")
        .Arc("partial0", "gr0").Arc("fork_B", "gr0").Arc("gr0", "eat0")
        .Arc("eat0", "rel0").Arc("rel0", "think0").Arc("rel0", "fork_A").Arc("rel0", "fork_B")
        // Phil1: grabs fork_B (left), then fork_C (right)
        .Transition("gl1").Transition("gr1").Transition("rel1")
        .Arc("think1", "gl1").Arc("fork_B", "gl1").Arc("gl1", "partial1")
        .Arc("partial1", "gr1").Arc("fork_C", "gr1").Arc("gr1", "eat1")
        .Arc("eat1", "rel1").Arc("rel1", "think1").Arc("rel1", "fork_B").Arc("rel1", "fork_C")
        // Phil2: grabs fork_C (left), then fork_A (right)
        .Transition("gl2").Transition("gr2").Transition("rel2")
        .Arc("think2", "gl2").Arc("fork_C", "gl2").Arc("gl2", "partial2")
        .Arc("partial2", "gr2").Arc("fork_A", "gr2").Arc("gr2", "eat2")
        .Arc("eat2", "rel2").Arc("rel2", "think2").Arc("rel2", "fork_C").Arc("rel2", "fork_A")
        .Build();

    [Fact]
    public void DiningPhilosophers3_NonAtomic_IsBoundedAndCanDeadlock()
    {
        var net = DiningPhilosophers3NonAtomic();
        var (ss, _, _, _, _) = Analyse(net);

        Assert.False(ss.HasErrors);
        Assert.False(ss.IsTruncated);
        Assert.True(ss.IsBounded());
        Assert.False(ss.IsDeadlockFree());
        Assert.False(ss.IsLive(net.Transitions.Count));
    }

    [Fact]
    public void DiningPhilosophers3_NonAtomic_DeadlockStateExists()
    {
        var net = DiningPhilosophers3NonAtomic();
        var (ss, _, _, _, _) = Analyse(net);
        var edges = ss.GetEdges();

        // At least one reachable state with no outgoing edges
        bool hasDeadlock = Enumerable.Range(0, ss.States.Count).Any(i => edges[i].Count == 0);
        Assert.True(hasDeadlock);
    }

    [Fact]
    public void DiningPhilosophers3_NonAtomic_AllThreeHoldOneFork_IsDeadlockState()
    {
        // The circular-wait deadlock: each philosopher holds their left fork.
        // partial0=1, partial1=1, partial2=1, all forks consumed.
        var net = DiningPhilosophers3NonAtomic();
        var (ss, _, _, _, _) = Analyse(net);

        var places = net.Places.ToList();
        int p0 = places.FindIndex(p => p.Id == "partial0");
        int p1 = places.FindIndex(p => p.Id == "partial1");
        int p2 = places.FindIndex(p => p.Id == "partial2");

        // Must reach a state where all three hold one fork
        Assert.Contains(ss.States, s => s[p0] == 1 && s[p1] == 1 && s[p2] == 1);
    }

    [Fact]
    public void DiningPhilosophers3_NonAtomic_HasCyclesAndTraps()
    {
        var net = DiningPhilosophers3NonAtomic();
        var (_, _, _, trap, cyc) = Analyse(net);

        Assert.True(cyc.Cycles.Count > 0);
        Assert.False(trap.HasErrors);
        Assert.True(trap.Traps.Count > 0);
        Assert.True(trap.Cotraps.Count > 0);
    }

    // ── Bounded Buffer (producer-consumer with N-capacity buffer) ────────────
    // Producer can only produce when buffer is not full (controlled by semaphore place).
    // Consumer can only consume when buffer is not empty.
    // capacity = 3

    private static PetriNetSnapshot BoundedBuffer(int capacity = 3)
    {
        var b = new NetBuilder()
            .Place("slots", tokens: capacity)  // free slots
            .Place("items", tokens: 0)         // filled slots
            .Transition("produce")
            .Transition("consume")
            .Arc("slots", "produce").Arc("produce", "items")
            .Arc("items", "consume").Arc("consume", "slots");
        return b.Build();
    }

    [Fact]
    public void BoundedBuffer_StateCountEqualsCapacityPlusOne()
    {
        const int cap = 3;
        var net = BoundedBuffer(cap);
        var (ss, _, _, _, _) = Analyse(net);

        Assert.False(ss.HasErrors);
        Assert.False(ss.IsTruncated);
        // States: 0..cap items in buffer = cap+1 states
        Assert.Equal(cap + 1, ss.States.Count);
    }

    [Fact]
    public void BoundedBuffer_NeverExceedsCapacity()
    {
        const int cap = 3;
        var net = BoundedBuffer(cap);
        var (ss, _, _, _, _) = Analyse(net);

        foreach (var state in ss.States)
            Assert.True(state.Sum() == cap, $"Token sum must always equal capacity {cap}");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(10)]
    public void BoundedBuffer_ScalesCorrectly(int capacity)
    {
        var net = BoundedBuffer(capacity);
        var (ss, _, _, _, _) = Analyse(net);

        Assert.Equal(capacity + 1, ss.States.Count);
        Assert.True(ss.IsDeadlockFree());
        Assert.True(ss.IsReversible());
    }

    // ── Token Ring (5 stations) ──────────────────────────────────────────────
    // A single token circulates through 5 stations in a ring.
    // Only the station holding the token can perform work.
    // Expected: safe, bounded, live, reversible, conservative.

    private static PetriNetSnapshot TokenRing(int n = 5)
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

    [Fact]
    public void TokenRing5_ExactlyNStates()
    {
        const int n = 5;
        var net = TokenRing(n);
        var (ss, _, _, _, _) = Analyse(net);

        Assert.False(ss.HasErrors);
        Assert.Equal(n, ss.States.Count);  // one state per token position
    }

    [Fact]
    public void TokenRing5_SafeLiveReversible()
    {
        var net = TokenRing(5);
        var (ss, _, _, _, _) = Analyse(net);

        Assert.True(ss.IsSafe());
        Assert.True(ss.IsDeadlockFree());
        Assert.True(ss.IsReversible());
        Assert.True(ss.IsLive(net.Transitions.Count));
    }

    [Fact]
    public void TokenRing5_ConservativeOneTokenAlways()
    {
        var net = TokenRing(5);
        var (ss, _, inv, _, _) = Analyse(net);

        foreach (var state in ss.States)
            Assert.Equal(1, state.Sum());

        Assert.True(inv.PInvariants.Count > 0);
    }
}
