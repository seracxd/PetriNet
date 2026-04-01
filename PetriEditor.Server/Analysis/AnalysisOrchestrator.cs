using Analysis;
using Analysis.Algorithms;
using Analysis.Engines;
using PetriEditor.Server.Analysis;
using PetriEditor.Shared.Contracts;
using PetriEditor.Shared.Mapping;

namespace PetriEditor.Server.Analysis;

/// <summary>
/// Runs all analysis engines in sequence and reports progress after each stage.
/// Called by <see cref="Hubs.AnalysisHub"/> on a background thread.
///
/// Progress is reported via <paramref name="progress"/>; each message carries
/// a stage name and a completion percentage so the client can update a progress bar.
/// </summary>
public sealed class AnalysisOrchestrator
{
    public async Task<AnalysisResultDto> RunAsync(
        PetriNetDto                          dto,
        IProgress<AnalysisProgressMessage>?  progress,
        CancellationToken                    ct)
    {
        var net    = PetriNetMapper.ToSnapshot(dto);
        var report = new AnalysisReport { Net = net };

        await Task.Run(() =>
        {
            // ── State space ───────────────────────────────────────────────
            ct.ThrowIfCancellationRequested();
            progress?.Report(new(AnalysisProgressMessage.StageStateSpace, 5, null));

            var ss = new StateSpaceAnalysis();
            ss.Build(net);
            report.StateSpace = ss;

            // ── Invariants ────────────────────────────────────────────────
            ct.ThrowIfCancellationRequested();
            progress?.Report(new(AnalysisProgressMessage.StageInvariants, 30, null));

            var inv = new InvariantAnalysis();
            inv.Compute(net);
            report.Invariants = inv;

            // ── Classification ────────────────────────────────────────────
            ct.ThrowIfCancellationRequested();
            progress?.Report(new(AnalysisProgressMessage.StageClassification, 50, null));

            var cls = new ClassificationAnalysis();
            cls.Compute(net);
            report.Classification = cls;

            // ── Cycles / traps ────────────────────────────────────────────
            ct.ThrowIfCancellationRequested();
            progress?.Report(new(AnalysisProgressMessage.StageCycles, 65, null));

            var cyc = new CyclesAnalysis();
            cyc.Compute(net);
            report.Cycles = cyc;

            var tc = new TrapCotrapAnalysis();
            tc.Compute(net);
            report.TrapCotrap = tc;

            // ── Reachability tree ─────────────────────────────────────────
            ct.ThrowIfCancellationRequested();
            progress?.Report(new(AnalysisProgressMessage.StageReachTree, 70, null));

            var rt = new ReachabilityTreeBuilder();
            rt.Build(net, ct);
            report.ReachabilityTree = rt;

            // ── Coverability tree (Karp-Miller) ───────────────────────────
            ct.ThrowIfCancellationRequested();
            progress?.Report(new(AnalysisProgressMessage.StageCoverTree, 76, null));

            var ct2 = new CoverabilityTreeBuilder();
            ct2.Build(net, ct);
            report.CoverabilityTree = ct2;

            // ── Property tests ────────────────────────────────────────────
            ct.ThrowIfCancellationRequested();
            progress?.Report(new(AnalysisProgressMessage.StagePropertyTests, 83, null));

            var bundle = new AnalysisBundle
            {
                Net            = net,
                StateSpace     = ss,
                Invariants     = inv,
                Classification = cls,
                Cycles         = cyc,
                TrapCotrap     = tc,
            };

            var results = bundle.PropertyResults;
            results[NetProperty.Liveness]        = SafeRun(NetProperty.Liveness,        () => new LivenessTest().Run(bundle));
            results[NetProperty.Boundedness]     = SafeRun(NetProperty.Boundedness,     () => new BoundednessTest().Run(bundle));
            results[NetProperty.Safety]          = SafeRun(NetProperty.Safety,          () => new SafetyTest().Run(bundle));
            results[NetProperty.Conservativeness] = SafeRun(NetProperty.Conservativeness, () => new ConservativenessTest().Run(bundle));
            results[NetProperty.Repetitiveness]  = SafeRun(NetProperty.Repetitiveness,  () => new RepetitivenessTest().Run(bundle));
            results[NetProperty.DeadlockFree]    = SafeRun(NetProperty.DeadlockFree,    () => new DeadlockFreeTest().Run(bundle));
            results[NetProperty.Reversibility]   = SafeRun(NetProperty.Reversibility,   () => new ReversibilityTest().Run(bundle));
            report.PropertyResults = results;

        }, ct);

        progress?.Report(new(AnalysisProgressMessage.StageComplete, 100, null));
        return AnalysisResultMapper.ToDto(report);
    }

    private static PropertyTestResult SafeRun(NetProperty property, Func<PropertyTestResult> action)
    {
        try
        {
            return action();
        }
        catch (Exception ex)
        {
            return new PropertyTestResult(
                property,
                TestResultStatus.Undecidable,
                [$"{property} test could not complete due to an internal error."],
                [$"{ex.GetType().Name}: {ex.Message}"]);
        }
    }
}
