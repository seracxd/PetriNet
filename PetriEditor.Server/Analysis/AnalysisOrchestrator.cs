using Analysis;
using Analysis.Algorithms;
using Analysis.Engines;
using Microsoft.Extensions.Logging;
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
    private readonly ILogger<AnalysisOrchestrator> _logger;

    public AnalysisOrchestrator(ILogger<AnalysisOrchestrator> logger)
    {
        _logger = logger;
    }

    public async Task<AnalysisResultDto> RunAsync(
        PetriNetDto                          dto,
        IProgress<AnalysisProgressMessage>?  progress,
        CancellationToken                    ct)
    {
        var net    = PetriNetMapper.ToSnapshot(dto);
        var report = new AnalysisReport { Net = net };

        bool cancelled = false;

        await Task.Run(() =>
        {
            // ── Stage 1: Coverability tree (Karp-Miller) ─────────────────────
            if (ct.IsCancellationRequested) { cancelled = true; return; }
            progress?.Report(new(AnalysisProgressMessage.StageStateSpace, 5, null));

            // Special arcs (inhibitor/reset) violate monotone firing semantics that
            // Karp-Miller assumes. For those nets we run plain bounded reachability
            // with cycle detection instead — correct results, no ω verdicts.
            // The cap is tighter (5K vs 100K) so we don't spend 60s on an unbounded
            // special-arc net only to truncate. The user can refine the net.
            bool hasSpecialArcs = net.Arcs.Any(a => a.ArcType != PnArcType.Normal);
            int cbCap = hasSpecialArcs ? 5_000 : CoverabilityTreeBuilder.MaxNodes;
            var cb = new CoverabilityTreeBuilder();
            cb.Build(net, ct, cbCap, disableOmegaAcceleration: hasSpecialArcs);
            if (!cb.HasErrors || cb.IsTruncated) report.CoverabilityTree = cb;
            if (ct.IsCancellationRequested) { cancelled = true; return; }

            bool isUnbounded = cb.Nodes.Any(n => n.Marking.Any(v => v == CoverabilityTreeBuilder.Omega));

            // For special-arc nets we skipped ω-acceleration, so a truncated cb means
            // the state space is too large to compute exhaustively. Treat that the
            // same as "unbounded" downstream so we don't burn time on engines that
            // need a complete state space (and would time out on large nets).
            bool stateSpaceTooLarge = isUnbounded || (hasSpecialArcs && cb.IsTruncated);

            // ── Stage 2: State-space (skip if unbounded or too large) ─────────
            var ss = new StateSpaceAnalysis();
            ss.Build(net, ct, stateSpaceTooLarge ? 500 : StateSpaceAnalysis.MaxStates);
            report.StateSpace = ss;
            if (ct.IsCancellationRequested) { cancelled = true; return; }

            progress?.Report(new(AnalysisProgressMessage.StageInvariants, 30, null));
            var inv = new InvariantAnalysis();
            if (!stateSpaceTooLarge)
                inv.Compute(net);
            else
                inv.SkipUnbounded();
            report.Invariants = inv;
            if (ct.IsCancellationRequested) { cancelled = true; return; }

            progress?.Report(new(AnalysisProgressMessage.StageClassification, 50, null));
            var cls = new ClassificationAnalysis();
            cls.Compute(net);
            report.Classification = cls;
            if (ct.IsCancellationRequested) { cancelled = true; return; }

            progress?.Report(new(AnalysisProgressMessage.StageCycles, 65, null));
            var cyc = new CyclesAnalysis();
            cyc.Compute(net);
            report.Cycles = cyc;
            var tc = new TrapCotrapAnalysis();
            tc.Compute(net, ct);
            report.TrapCotrap = tc;
            if (ct.IsCancellationRequested) { cancelled = true; return; }

            progress?.Report(new(AnalysisProgressMessage.StagePropertyTests, 70, null));
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
            results[NetProperty.Liveness]         = SafeRun(NetProperty.Liveness,         () => new LivenessTest().Run(bundle));
            results[NetProperty.Boundedness]      = SafeRun(NetProperty.Boundedness,       () => new BoundednessTest().Run(bundle));
            results[NetProperty.Safety]           = SafeRun(NetProperty.Safety,            () => new SafetyTest().Run(bundle));
            results[NetProperty.Conservativeness] = SafeRun(NetProperty.Conservativeness,  () => new ConservativenessTest().Run(bundle));
            results[NetProperty.Repetitiveness]   = SafeRun(NetProperty.Repetitiveness,    () => new RepetitivenessTest().Run(bundle));
            results[NetProperty.DeadlockFree]     = SafeRun(NetProperty.DeadlockFree,      () => new DeadlockFreeTest().Run(bundle));
            results[NetProperty.Reversibility]    = SafeRun(NetProperty.Reversibility,     () => new ReversibilityTest().Run(bundle));
            report.PropertyResults = results;
            if (ct.IsCancellationRequested) { cancelled = true; return; }

            progress?.Report(new(AnalysisProgressMessage.StagePropertyTests, 90, null));

            if (!stateSpaceTooLarge)
            {
                var rt = new ReachabilityTreeBuilder();
                rt.Build(net, ct, StateSpaceAnalysis.MaxStates);
                if (!rt.HasErrors || rt.IsTruncated) report.ReachabilityTree = rt;
            }
        });

        if (cancelled)
            throw new OperationCanceledException(ct);

        return AnalysisResultMapper.ToDto(report);
    }

    public async Task<GraphResultDto> RunGraphAsync(
        PetriNetDto       dto,
        CancellationToken ct)
    {
        var net = PetriNetMapper.ToSnapshot(dto);

        CoverabilityTreeDto?  coverDto = null;
        StateSpaceSummaryDto? ssDto    = null;
        string? error = null;

        await Task.Run(() =>
        {
            try
            {
                // Always use the coverability tree — it handles both bounded and unbounded nets.
                // For bounded nets the result is identical to a reachability tree (no ω appears).
                // Inhibitor/reset arcs disable ω-acceleration so the result stays correct.
                // Tighter cap for special-arc nets — bounded reachability can otherwise
                // explode and exceed the request deadline before any progress reaches the client.
                bool hasSpecialArcs = net.Arcs.Any(a => a.ArcType != PnArcType.Normal);
                int cbCap = hasSpecialArcs ? 5_000 : AnalysisLimits.MaxMarkings;
                var cb = new CoverabilityTreeBuilder();
                cb.Build(net, ct, cbCap, disableOmegaAcceleration: hasSpecialArcs);
                // Genuine failure (no usable tree): surface as ErrorMessage.
                if (cb.HasErrors && !cb.IsTruncated) { error = cb.ErrorMessage; return; }
                // Truncation is reflected via StateSpaceSummaryDto.ExceededLimit, not ErrorMessage.

                coverDto = AnalysisResultMapper.BuildCoverabilityTreeDto(net, cb);

                bool isUnbounded = cb.Nodes.Any(n => n.Marking.Any(v => v == CoverabilityTreeBuilder.Omega));
                bool isDeadlockFree = cb.Nodes.Any() && !cb.Nodes.Any(n => n.IsDeadlock && !n.IsDuplicate);

                ssDto = new StateSpaceSummaryDto(
                    StateCount:     cb.Nodes.Count,
                    IsBounded:      !isUnbounded,
                    IsSafe:         false,   // coverability tree doesn't track max tokens
                    IsDeadlockFree: isDeadlockFree,
                    IsReversible:   false,
                    ExceededLimit:  cb.IsTruncated);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Graph analysis engine threw unexpectedly");
                error = ex.Message;
            }
        });

        return new GraphResultDto(null, null, coverDto, error, ssDto);
    }

    private PropertyTestResult SafeRun(NetProperty property, Func<PropertyTestResult> action)
    {
        try
        {
            return action();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Property test {Property} threw unexpectedly", property);
            return new PropertyTestResult(
                property,
                TestResultStatus.Undecidable,
                [$"{property} test could not complete due to an internal error."],
                [$"{ex.GetType().Name}: {ex.Message}"]);
        }
    }
}
