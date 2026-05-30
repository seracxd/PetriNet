using Analysis;
using Analysis.Algorithms;
using PetriEditor.Client.Services;
using PetriEditor.Shared.Mapping;
using PetriEditor.Tests.Helpers;

namespace PetriEditor.Tests;

/// <summary>
/// Loads the curated nets under <c>/test-nets</c> through the real serializer and
/// the orchestrator-equivalent bundle, then asserts the verdicts documented in
/// <c>test-nets/README.md</c>. This keeps the sample nets honest: if an engine
/// change alters a verdict, the matching row here fails.
/// </summary>
public class TestNetFilesTests
{
    private static readonly BrowserSerializationService Serializer = new();

    private static AnalysisBundle Load(string fileName)
    {
        var path = Path.Combine(TestNetsDir(), fileName);
        var dto  = Serializer.DeserializeFromJson(File.ReadAllText(path));
        var net  = PetriNetMapper.ToSnapshot(dto);
        var b    = OrchestratorBundleBuilder.Build(net);
        // DeadlockFree reads Liveness from PropertyResults — populate it first.
        b.PropertyResults[NetProperty.Liveness] = new LivenessTest().Run(b);
        return b;
    }

    private static string TestNetsDir()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "PetriNet.slnx")))
            d = d.Parent;
        if (d == null) throw new DirectoryNotFoundException("Could not locate repo root (PetriNet.slnx).");
        return Path.Combine(d.FullName, "test-nets");
    }

    [Fact]
    public void AllTestNetFiles_LoadAndParse()
    {
        var files = Directory.GetFiles(TestNetsDir(), "*.json");
        Assert.NotEmpty(files);
        foreach (var f in files)
        {
            var dto = Serializer.DeserializeFromJson(File.ReadAllText(f));
            Assert.NotEmpty(dto.Places);
            Assert.NotEmpty(dto.Transitions);
        }
    }

    // status: 0 = Undecidable, "Pass", "Fail"
    private static void AssertVerdicts(
        string file,
        TestResultStatus bound,
        TestResultStatus safe,
        TestResultStatus live,
        TestResultStatus deadlockFree,
        TestResultStatus reversible)
    {
        var b = Load(file);
        Assert.Equal(bound,        new BoundednessTest().Run(b).Status);
        Assert.Equal(safe,         new SafetyTest().Run(b).Status);
        Assert.Equal(live,         b.PropertyResults[NetProperty.Liveness].Status);
        Assert.Equal(deadlockFree, new DeadlockFreeTest().Run(b).Status);
        Assert.Equal(reversible,   new ReversibilityTest().Run(b).Status);
    }

    // ── Bounded, well-behaved nets ───────────────────────────────────────────

    [Fact]
    public void BoundedLiveCycle_EverythingHolds() => AssertVerdicts(
        "bounded-live-cycle.json",
        bound: TestResultStatus.Pass, safe: TestResultStatus.Pass, live: TestResultStatus.Pass,
        deadlockFree: TestResultStatus.Pass, reversible: TestResultStatus.Pass);

    [Fact]
    public void SharedResource_BoundedLiveButUnsafe() => AssertVerdicts(
        "shared-resource.json",
        bound: TestResultStatus.Pass, safe: TestResultStatus.Fail, live: TestResultStatus.Pass,
        deadlockFree: TestResultStatus.Pass, reversible: TestResultStatus.Pass);

    [Fact]
    public void DiningPhilosophers_AtomicGrab_EverythingHolds() => AssertVerdicts(
        "dining-philosophers-4.json",
        bound: TestResultStatus.Pass, safe: TestResultStatus.Pass, live: TestResultStatus.Pass,
        deadlockFree: TestResultStatus.Pass, reversible: TestResultStatus.Pass);

    [Fact]
    public void DeadlockSequential_DeadlocksAndIrreversible() => AssertVerdicts(
        "deadlock-sequential.json",
        bound: TestResultStatus.Pass, safe: TestResultStatus.Pass, live: TestResultStatus.Fail,
        deadlockFree: TestResultStatus.Fail, reversible: TestResultStatus.Fail);

    [Fact]
    public void BoundedUnsafeMerge_BoundedButUnsafe() => AssertVerdicts(
        "bounded-unsafe-merge.json",
        bound: TestResultStatus.Pass, safe: TestResultStatus.Fail, live: TestResultStatus.Fail,
        deadlockFree: TestResultStatus.Fail, reversible: TestResultStatus.Fail);

    // ── The unbounded regression cases: Bounded must be Fail, never Undecidable ─

    [Fact]
    public void UnboundedProducer_BoundednessIsFailNotInconclusive() => AssertVerdicts(
        "unbounded-producer.json",
        bound: TestResultStatus.Fail, safe: TestResultStatus.Fail, live: TestResultStatus.Pass,
        deadlockFree: TestResultStatus.Pass, reversible: TestResultStatus.Fail);

    [Fact]
    public void UnboundedSourceTransition_BoundednessIsFailNotInconclusive() => AssertVerdicts(
        "unbounded-source-transition.json",
        bound: TestResultStatus.Fail, safe: TestResultStatus.Fail, live: TestResultStatus.Pass,
        deadlockFree: TestResultStatus.Pass, reversible: TestResultStatus.Fail);

    [Fact]
    public void HeavyweightStateMachine_BoundednessIsFailNotPass() => AssertVerdicts(
        "heavyweight-state-machine.json",
        bound: TestResultStatus.Fail, safe: TestResultStatus.Fail, live: TestResultStatus.Pass,
        deadlockFree: TestResultStatus.Pass, reversible: TestResultStatus.Fail);

    [Fact]
    public void SaturatingInhibitor_BoundednessIsFailNotInconclusive() => AssertVerdicts(
        "unbounded-saturating-inhibitor.json",
        bound: TestResultStatus.Fail, safe: TestResultStatus.Fail, live: TestResultStatus.Pass,
        deadlockFree: TestResultStatus.Pass, reversible: TestResultStatus.Fail);

    [Fact]
    public void AllUnboundedNets_AreFlaggedUnbounded()
    {
        foreach (var f in new[]
        {
            "unbounded-producer.json",
            "unbounded-source-transition.json",
            "heavyweight-state-machine.json",
            "unbounded-saturating-inhibitor.json",
        })
            Assert.True(Load(f).IsUnbounded, $"{f} should be flagged unbounded.");
    }
}
