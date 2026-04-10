using Analysis;
using Analysis.Engines;
using PetriEditor.Tests.Helpers;

namespace PetriEditor.Tests;

public class StateSpaceTests
{
    // ── helpers ───────────────────────────────────────────────────────────

    private static StateSpaceAnalysis Build(PetriNetSnapshot net)
    {
        var ss = new StateSpaceAnalysis();
        ss.Build(net);
        return ss;
    }

    // ── Basic firing ──────────────────────────────────────────────────────

    [Fact]
    public void SingleTransition_Fires_AndReachesExpectedMarkings()
    {
        // P1(1) --T1--> P2
        var net = new NetBuilder()
            .Place("P1", tokens: 1)
            .Place("P2", tokens: 0)
            .Transition("T1")
            .Arc("P1", "T1").Arc("T1", "P2")
            .Build();

        var ss = Build(net);

        Assert.False(ss.HasErrors);
        Assert.Equal(2, ss.States.Count); // {1,0} and {0,1}
    }

    [Fact]
    public void NoTokens_TransitionNotEnabled_OneState()
    {
        var net = new NetBuilder()
            .Place("P1", tokens: 0)
            .Place("P2", tokens: 0)
            .Transition("T1")
            .Arc("P1", "T1").Arc("T1", "P2")
            .Build();

        var ss = Build(net);

        Assert.Single(ss.States); // only initial state, T1 never fires
        Assert.True(ss.IsDeadlockFree() == false); // deadlock at initial state
    }

    // ── Inhibitor arcs ────────────────────────────────────────────────────

    [Fact]
    public void InhibitorArc_BlocksTransition_WhenPlaceHasTokens()
    {
        // P1(1) --inhibitor--> T1 ; P2(0) --normal--> T1 ; T1 --> P3
        // T1 should NOT fire because P1 has 1 token
        var net = new NetBuilder()
            .Place("P1", tokens: 1)
            .Place("P2", tokens: 1)
            .Place("P3", tokens: 0)
            .Transition("T1")
            .Inhibitor("P1", "T1")
            .Arc("P2", "T1").Arc("T1", "P3")
            .Build();

        var ss = Build(net);

        Assert.Single(ss.States); // T1 blocked, no new states
    }

    [Fact]
    public void InhibitorArc_AllowsTransition_WhenPlaceEmpty()
    {
        // P1(0) --inhibitor--> T1 ; P2(1) --normal--> T1 ; T1 --> P3
        // T1 SHOULD fire
        var net = new NetBuilder()
            .Place("P1", tokens: 0)
            .Place("P2", tokens: 1)
            .Place("P3", tokens: 0)
            .Transition("T1")
            .Inhibitor("P1", "T1")
            .Arc("P2", "T1").Arc("T1", "P3")
            .Build();

        var ss = Build(net);

        Assert.Equal(2, ss.States.Count);
        // Final state: P1=0, P2=0, P3=1
        Assert.Contains(ss.States, s => s[0] == 0 && s[1] == 0 && s[2] == 1);
    }

    // ── Reset arcs ────────────────────────────────────────────────────────

    [Fact]
    public void ResetArc_ClearsPlace_OnFire()
    {
        // P1(3) --reset--> T1 ; T1 --> P2
        var net = new NetBuilder()
            .Place("P1", tokens: 3)
            .Place("P2", tokens: 0)
            .Transition("T1")
            .Reset("P1", "T1").Arc("T1", "P2")
            .Build();

        var ss = Build(net);

        // After T1 fires: P1=0, P2=1
        Assert.Contains(ss.States, s => s[0] == 0 && s[1] == 1);
    }

    [Fact]
    public void ResetArc_RequiresWeight_ToBeEnabled()
    {
        // Reset arc with weight 2: P1 must have >= 2 tokens to fire
        var net = new NetBuilder()
            .Place("P1", tokens: 1)
            .Place("P2", tokens: 0)
            .Transition("T1")
            .Arc("P1", "T1", weight: 2, type: PnArcType.Reset).Arc("T1", "P2")
            .Build();

        var ss = Build(net);

        Assert.Single(ss.States); // T1 not enabled
    }

    // ── Priority ──────────────────────────────────────────────────────────

    [Fact]
    public void Priority_HigherPriorityBlocksLower()
    {
        // P1(1) -> T1 (priority 1) -> P2
        // P1(1) -> T2 (priority 2) -> P3
        // Both enabled from initial marking, but only T2 (higher priority) should fire
        var net = new NetBuilder()
            .Place("P1", tokens: 1)
            .Place("P2", tokens: 0)
            .Place("P3", tokens: 0)
            .Transition("T1", priority: 1)
            .Transition("T2", priority: 2)
            .Arc("P1", "T1").Arc("T1", "P2")
            .Arc("P1", "T2").Arc("T2", "P3")
            .Build();

        var ss = Build(net);

        // Only T2 fires: reachable states are {P1=1,P2=0,P3=0} and {P1=0,P2=0,P3=1}
        Assert.Equal(2, ss.States.Count);
        Assert.Contains(ss.States, s => s[0] == 0 && s[1] == 0 && s[2] == 1); // T2 fired
        Assert.DoesNotContain(ss.States, s => s[1] == 1); // T1 never fires
    }

    [Fact]
    public void Priority_EqualPriority_BothTransitionsFire()
    {
        // Same net but both T1 and T2 have equal priority — both reachable
        var net = new NetBuilder()
            .Place("P1", tokens: 1)
            .Place("P2", tokens: 0)
            .Place("P3", tokens: 0)
            .Transition("T1", priority: 1)
            .Transition("T2", priority: 1)
            .Arc("P1", "T1").Arc("T1", "P2")
            .Arc("P1", "T2").Arc("T2", "P3")
            .Build();

        var ss = Build(net);

        Assert.Equal(3, ss.States.Count); // initial + T1-result + T2-result
        Assert.Contains(ss.States, s => s[1] == 1); // T1 reachable
        Assert.Contains(ss.States, s => s[2] == 1); // T2 reachable
    }

    [Fact]
    public void Priority_ZeroPriority_AllTransitionsFire()
    {
        // All priorities 0 — no filtering, same as equal-priority case
        var net = new NetBuilder()
            .Place("P1", tokens: 1)
            .Place("P2", tokens: 0)
            .Place("P3", tokens: 0)
            .Transition("T1", priority: 0)
            .Transition("T2", priority: 0)
            .Arc("P1", "T1").Arc("T1", "P2")
            .Arc("P1", "T2").Arc("T2", "P3")
            .Build();

        var ss = Build(net);

        Assert.Equal(3, ss.States.Count);
    }

    [Fact]
    public void Priority_LowPriorityBecomesFireable_AfterHighDisabled()
    {
        // P1(1) -> T_high (priority 2) -> P2
        // P3(1) -> T_low  (priority 1) -> P4
        // Initially only T_high is enabled (T_low's input P3 has tokens, but T_high blocks nothing here
        // — they have different inputs, so both are enabled but T_high wins.
        // After T_high fires (P1 emptied), T_low fires from P3.
        var net = new NetBuilder()
            .Place("P1", tokens: 1)
            .Place("P2", tokens: 0)
            .Place("P3", tokens: 1)
            .Place("P4", tokens: 0)
            .Transition("T_high", priority: 2)
            .Transition("T_low",  priority: 1)
            .Arc("P1", "T_high").Arc("T_high", "P2")
            .Arc("P3", "T_low").Arc("T_low", "P4")
            .Build();

        var ss = Build(net);

        // State sequence: {1,0,1,0} -> T_high -> {0,1,1,0} -> T_low -> {0,1,0,1}
        Assert.Contains(ss.States, s => s[0] == 0 && s[1] == 1 && s[2] == 0 && s[3] == 1);
        // T_low must NOT fire from the initial state (T_high is enabled and has higher priority)
        Assert.DoesNotContain(ss.States, s => s[0] == 1 && s[3] == 1);
    }

    // ── Normal + Inhibitor arc pair ───────────────────────────────────────

    [Fact]
    public void NormalAndInhibitor_BothPresent_CorrectSemantics()
    {
        // P1(0) --normal--> T1 (requires 1 token — not enabled)
        // P1(0) --inhibitor--> T2 (fires when P1 empty — enabled)
        // T1 --> P2 ; T2 --> P3
        var net = new NetBuilder()
            .Place("P1", tokens: 0)
            .Place("P2", tokens: 0)
            .Place("P3", tokens: 0)
            .Place("P_src", tokens: 1)
            .Transition("T1")
            .Transition("T2")
            .Arc("P1", "T1")          // normal: needs P1 token
            .Arc("P_src", "T1")       // also needs P_src
            .Arc("P_src", "T2")       // T2 also needs a token source
            .Inhibitor("P1", "T2")    // inhibitor: fires only when P1 empty
            .Arc("T1", "P2")
            .Arc("T2", "P3")
            .Build();

        var ss = Build(net);

        // T1 blocked (P1=0), T2 enabled (P1=0 satisfies inhibitor, P_src=1)
        // After T2: P_src=0, P3=1 — then deadlock
        Assert.Contains(ss.States, s => s[2] == 1); // P3 gets a token
        Assert.DoesNotContain(ss.States, s => s[1] == 1); // P2 never gets a token
    }

    // ── Normal + Reset arc pair ───────────────────────────────────────────

    [Fact]
    public void NormalAndReset_BothPresent_CorrectSemantics()
    {
        // P1(3) --reset--> T1 ; P1(3) --normal--> T1 (weight 1) ; T1 --> P2
        // Normal consumes 1, Reset zeroes P1. Net effect: P1 goes from 3 to 0, P2 gets 1.
        var net = new NetBuilder()
            .Place("P1", tokens: 3)
            .Place("P2", tokens: 0)
            .Transition("T1")
            .Arc("P1", "T1", weight: 1, type: PnArcType.Normal)
            .Arc("P1", "T1", weight: 1, type: PnArcType.Reset)
            .Arc("T1", "P2")
            .Build();

        var ss = Build(net);

        // After T1: P1=0 (reset wins — applied after normal consumption), P2=1
        Assert.Contains(ss.States, s => s[0] == 0 && s[1] == 1);
    }
}
