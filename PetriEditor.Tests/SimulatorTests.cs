using Analysis.Simulation;
using Core.Models;

namespace PetriEditor.Tests;

public class SimulatorTests
{
    // ── Helpers ───────────────────────────────────────────────────────────

    private static PetriNetSimulator MakeSimulator(
        IEnumerable<PetriNetSimulator.PlaceInfo>      places,
        IEnumerable<PetriNetSimulator.TransitionInfo> transitions,
        IEnumerable<PetriNetSimulator.ArcInfo>        arcs)
    {
        var sim = new PetriNetSimulator();
        sim.Init(places, transitions, arcs);
        return sim;
    }

    /// Simple net: P1(1) ->T1-> P2
    private static PetriNetSimulator SimpleNet()
    {
        var places = new[]
        {
            new PetriNetSimulator.PlaceInfo("P1", "Place1", 1),
            new PetriNetSimulator.PlaceInfo("P2", "Place2", 0),
        };
        var transitions = new[]
        {
            new PetriNetSimulator.TransitionInfo("T1", "Trans1", 0),
        };
        var arcs = new[]
        {
            new PetriNetSimulator.ArcInfo("P1", "T1", PlaceIsSource: true,  Weight: 1, Type: ArcType.Normal),
            new PetriNetSimulator.ArcInfo("P2", "T1", PlaceIsSource: false, Weight: 1, Type: ArcType.Normal),
        };
        return MakeSimulator(places, transitions, arcs);
    }

    // ── Init ─────────────────────────────────────────────────────────────

    [Fact]
    public void Init_SetsIsInitialised()
    {
        var sim = SimpleNet();
        Assert.True(sim.IsInitialised);
    }

    [Fact]
    public void Init_SetsInitialMarking()
    {
        var sim = SimpleNet();
        Assert.Equal(1, sim.Marking["P1"]);
        Assert.Equal(0, sim.Marking["P2"]);
    }

    [Fact]
    public void Init_ClearsFiringHistory()
    {
        var sim = SimpleNet();
        sim.Fire("T1");
        sim.Init(sim.Places, sim.Transitions, sim.Arcs); // re-init
        Assert.Empty(sim.FiringHistory);
    }

    // ── IsEnabled ─────────────────────────────────────────────────────────

    [Fact]
    public void IsEnabled_True_WhenEnoughTokens()
    {
        var sim = SimpleNet();
        Assert.True(sim.IsEnabled("T1"));
    }

    [Fact]
    public void IsEnabled_False_WhenNoTokens()
    {
        var places = new[]
        {
            new PetriNetSimulator.PlaceInfo("P1", "P1", 0),
            new PetriNetSimulator.PlaceInfo("P2", "P2", 0),
        };
        var transitions = new[] { new PetriNetSimulator.TransitionInfo("T1", "T1", 0) };
        var arcs = new[]
        {
            new PetriNetSimulator.ArcInfo("P1", "T1", true,  1, ArcType.Normal),
            new PetriNetSimulator.ArcInfo("P2", "T1", false, 1, ArcType.Normal),
        };
        var sim = MakeSimulator(places, transitions, arcs);
        Assert.False(sim.IsEnabled("T1"));
    }

    [Fact]
    public void IsEnabled_Inhibitor_TrueWhenPlaceEmpty()
    {
        var places = new[]
        {
            new PetriNetSimulator.PlaceInfo("P1", "P1", 0),   // inhibitor place
            new PetriNetSimulator.PlaceInfo("P2", "P2", 1),   // normal input
            new PetriNetSimulator.PlaceInfo("P3", "P3", 0),
        };
        var transitions = new[] { new PetriNetSimulator.TransitionInfo("T1", "T1", 0) };
        var arcs = new[]
        {
            new PetriNetSimulator.ArcInfo("P1", "T1", true,  1, ArcType.Inhibitor),
            new PetriNetSimulator.ArcInfo("P2", "T1", true,  1, ArcType.Normal),
            new PetriNetSimulator.ArcInfo("P3", "T1", false, 1, ArcType.Normal),
        };
        var sim = MakeSimulator(places, transitions, arcs);
        Assert.True(sim.IsEnabled("T1"));
    }

    [Fact]
    public void IsEnabled_Inhibitor_FalseWhenPlaceHasTokens()
    {
        var places = new[]
        {
            new PetriNetSimulator.PlaceInfo("P1", "P1", 1),   // inhibitor place has token
            new PetriNetSimulator.PlaceInfo("P2", "P2", 1),
            new PetriNetSimulator.PlaceInfo("P3", "P3", 0),
        };
        var transitions = new[] { new PetriNetSimulator.TransitionInfo("T1", "T1", 0) };
        var arcs = new[]
        {
            new PetriNetSimulator.ArcInfo("P1", "T1", true,  1, ArcType.Inhibitor),
            new PetriNetSimulator.ArcInfo("P2", "T1", true,  1, ArcType.Normal),
            new PetriNetSimulator.ArcInfo("P3", "T1", false, 1, ArcType.Normal),
        };
        var sim = MakeSimulator(places, transitions, arcs);
        Assert.False(sim.IsEnabled("T1"));
    }

    [Fact]
    public void IsEnabled_ResetArc_DoesNotRequireTokens()
    {
        // Reset arc does not guard enabledness — T1 fires even with P1=0
        var places = new[]
        {
            new PetriNetSimulator.PlaceInfo("P1", "P1", 0),
            new PetriNetSimulator.PlaceInfo("P2", "P2", 0),
        };
        var transitions = new[] { new PetriNetSimulator.TransitionInfo("T1", "T1", 0) };
        var arcs = new[]
        {
            new PetriNetSimulator.ArcInfo("P1", "T1", true,  1, ArcType.Reset),
            new PetriNetSimulator.ArcInfo("P2", "T1", false, 1, ArcType.Normal),
        };
        var sim = MakeSimulator(places, transitions, arcs);
        Assert.True(sim.IsEnabled("T1"));
    }

    // ── Fire ──────────────────────────────────────────────────────────────

    [Fact]
    public void Fire_UpdatesMarking()
    {
        var sim = SimpleNet();
        bool fired = sim.Fire("T1");
        Assert.True(fired);
        Assert.Equal(0, sim.Marking["P1"]);
        Assert.Equal(1, sim.Marking["P2"]);
    }

    [Fact]
    public void Fire_AppendsFiringHistory()
    {
        var sim = SimpleNet();
        sim.Fire("T1");
        Assert.Equal(["T1"], sim.FiringHistory);
    }

    [Fact]
    public void Fire_ReturnsFalse_WhenNotEnabled()
    {
        var sim = SimpleNet();
        sim.Fire("T1"); // now P1=0
        bool fired = sim.Fire("T1");
        Assert.False(fired);
        Assert.Single(sim.FiringHistory); // only one firing recorded
    }

    [Fact]
    public void Fire_ResetArc_ZerosPlace()
    {
        var places = new[]
        {
            new PetriNetSimulator.PlaceInfo("P1", "P1", 5),
            new PetriNetSimulator.PlaceInfo("P2", "P2", 0),
        };
        var transitions = new[] { new PetriNetSimulator.TransitionInfo("T1", "T1", 0) };
        var arcs = new[]
        {
            new PetriNetSimulator.ArcInfo("P1", "T1", true,  1, ArcType.Reset),
            new PetriNetSimulator.ArcInfo("P2", "T1", false, 1, ArcType.Normal),
        };
        var sim = MakeSimulator(places, transitions, arcs);
        sim.Fire("T1");
        Assert.Equal(0, sim.Marking["P1"]);
        Assert.Equal(1, sim.Marking["P2"]);
    }

    [Fact]
    public void Fire_InhibitorArc_ConsumesNothing()
    {
        var places = new[]
        {
            new PetriNetSimulator.PlaceInfo("P1", "P1", 0),   // inhibitor — stays 0
            new PetriNetSimulator.PlaceInfo("P2", "P2", 1),
            new PetriNetSimulator.PlaceInfo("P3", "P3", 0),
        };
        var transitions = new[] { new PetriNetSimulator.TransitionInfo("T1", "T1", 0) };
        var arcs = new[]
        {
            new PetriNetSimulator.ArcInfo("P1", "T1", true,  1, ArcType.Inhibitor),
            new PetriNetSimulator.ArcInfo("P2", "T1", true,  1, ArcType.Normal),
            new PetriNetSimulator.ArcInfo("P3", "T1", false, 1, ArcType.Normal),
        };
        var sim = MakeSimulator(places, transitions, arcs);
        sim.Fire("T1");
        Assert.Equal(0, sim.Marking["P1"]); // inhibitor place unchanged
        Assert.Equal(0, sim.Marking["P2"]);
        Assert.Equal(1, sim.Marking["P3"]);
    }

    // ── Priority ──────────────────────────────────────────────────────────

    [Fact]
    public void GetEnabledTransitions_FiltersToHighestPriority()
    {
        var places = new[]
        {
            new PetriNetSimulator.PlaceInfo("P1", "P1", 1),
            new PetriNetSimulator.PlaceInfo("P2", "P2", 0),
            new PetriNetSimulator.PlaceInfo("P3", "P3", 0),
        };
        var transitions = new[]
        {
            new PetriNetSimulator.TransitionInfo("T_lo", "T_lo", 1),
            new PetriNetSimulator.TransitionInfo("T_hi", "T_hi", 2),
        };
        var arcs = new[]
        {
            new PetriNetSimulator.ArcInfo("P1", "T_lo", true,  1, ArcType.Normal),
            new PetriNetSimulator.ArcInfo("P2", "T_lo", false, 1, ArcType.Normal),
            new PetriNetSimulator.ArcInfo("P1", "T_hi", true,  1, ArcType.Normal),
            new PetriNetSimulator.ArcInfo("P3", "T_hi", false, 1, ArcType.Normal),
        };
        var sim = MakeSimulator(places, transitions, arcs);

        var enabled = sim.GetEnabledTransitions();
        Assert.Contains("T_hi", enabled);
        Assert.DoesNotContain("T_lo", enabled);
    }

    [Fact]
    public void GetEnabledTransitions_AllZeroPriority_ReturnsAll()
    {
        var places = new[]
        {
            new PetriNetSimulator.PlaceInfo("P1", "P1", 1),
            new PetriNetSimulator.PlaceInfo("P2", "P2", 0),
            new PetriNetSimulator.PlaceInfo("P3", "P3", 0),
        };
        var transitions = new[]
        {
            new PetriNetSimulator.TransitionInfo("T1", "T1", 0),
            new PetriNetSimulator.TransitionInfo("T2", "T2", 0),
        };
        var arcs = new[]
        {
            new PetriNetSimulator.ArcInfo("P1", "T1", true,  1, ArcType.Normal),
            new PetriNetSimulator.ArcInfo("P2", "T1", false, 1, ArcType.Normal),
            new PetriNetSimulator.ArcInfo("P1", "T2", true,  1, ArcType.Normal),
            new PetriNetSimulator.ArcInfo("P3", "T2", false, 1, ArcType.Normal),
        };
        var sim = MakeSimulator(places, transitions, arcs);

        var enabled = sim.GetEnabledTransitions();
        Assert.Contains("T1", enabled);
        Assert.Contains("T2", enabled);
    }

    // ── Reset ─────────────────────────────────────────────────────────────

    [Fact]
    public void Reset_RestoresInitialMarking()
    {
        var sim = SimpleNet();
        sim.Fire("T1");
        sim.Reset();
        Assert.Equal(1, sim.Marking["P1"]);
        Assert.Equal(0, sim.Marking["P2"]);
    }

    [Fact]
    public void Reset_ClearsFiringHistory()
    {
        var sim = SimpleNet();
        sim.Fire("T1");
        sim.Reset();
        Assert.Empty(sim.FiringHistory);
    }

    // ── Stop ──────────────────────────────────────────────────────────────

    [Fact]
    public void Stop_ClearsIsInitialised()
    {
        var sim = SimpleNet();
        sim.Stop();
        Assert.False(sim.IsInitialised);
        Assert.Empty(sim.Places);
        Assert.Empty(sim.Transitions);
        Assert.Empty(sim.Arcs);
    }

    // ── RewindToStep ─────────────────────────────────────────────────────

    [Fact]
    public void RewindToStep_NegativeOne_ClearsHistoryAndResetsMarking()
    {
        var places = new[]
        {
            new PetriNetSimulator.PlaceInfo("P1", "P1", 2),
            new PetriNetSimulator.PlaceInfo("P2", "P2", 0),
        };
        var transitions = new[] { new PetriNetSimulator.TransitionInfo("T1", "T1", 0) };
        var arcs = new[]
        {
            new PetriNetSimulator.ArcInfo("P1", "T1", true,  1, ArcType.Normal),
            new PetriNetSimulator.ArcInfo("P2", "T1", false, 1, ArcType.Normal),
        };
        var sim = MakeSimulator(places, transitions, arcs);
        sim.Fire("T1");
        sim.Fire("T1");

        sim.RewindToStep(-1);

        Assert.Empty(sim.FiringHistory);
        Assert.Equal(2, sim.Marking["P1"]);
        Assert.Equal(0, sim.Marking["P2"]);
    }

    [Fact]
    public void RewindToStep_Zero_KeepsOnlyFirstFiring()
    {
        var places = new[]
        {
            new PetriNetSimulator.PlaceInfo("P1", "P1", 3),
            new PetriNetSimulator.PlaceInfo("P2", "P2", 0),
        };
        var transitions = new[] { new PetriNetSimulator.TransitionInfo("T1", "T1", 0) };
        var arcs = new[]
        {
            new PetriNetSimulator.ArcInfo("P1", "T1", true,  1, ArcType.Normal),
            new PetriNetSimulator.ArcInfo("P2", "T1", false, 1, ArcType.Normal),
        };
        var sim = MakeSimulator(places, transitions, arcs);
        sim.Fire("T1"); // step 0
        sim.Fire("T1"); // step 1
        sim.Fire("T1"); // step 2

        sim.RewindToStep(0);

        Assert.Single(sim.FiringHistory);
        Assert.Equal(2, sim.Marking["P1"]);
        Assert.Equal(1, sim.Marking["P2"]);
    }

    [Fact]
    public void RewindToStep_PastEnd_Clamps()
    {
        var sim = SimpleNet();
        sim.Fire("T1");
        sim.RewindToStep(100); // clamps to last valid step
        Assert.Single(sim.FiringHistory);
    }

    // ── Name lookup ───────────────────────────────────────────────────────

    [Fact]
    public void GetPlaceName_ReturnsName()
    {
        var sim = SimpleNet();
        Assert.Equal("Place1", sim.GetPlaceName("P1"));
    }

    [Fact]
    public void GetPlaceName_UnknownId_ReturnsId()
    {
        var sim = SimpleNet();
        Assert.Equal("unknown", sim.GetPlaceName("unknown"));
    }

    [Fact]
    public void GetTransitionName_ReturnsName()
    {
        var sim = SimpleNet();
        Assert.Equal("Trans1", sim.GetTransitionName("T1"));
    }
}
