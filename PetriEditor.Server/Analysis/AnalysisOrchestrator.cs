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

    // Sub-budgets carved out of the hub-level deadline. A slow engine cannot
    // starve the rest: if one of these elapses, the engine is cancelled and
    // subsequent engines still get a chance to run. The hub's outer deadline
    // remains the final wall-clock ceiling.
    private static readonly TimeSpan _coverabilityBudget = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan _stateSpaceBudget   = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan _structuralBudget   = TimeSpan.FromSeconds(10);  // invariants + cycles + traps + classification
    private static readonly TimeSpan _propertyBudget     = TimeSpan.FromSeconds(8);

    public AnalysisOrchestrator(ILogger<AnalysisOrchestrator> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Run <paramref name="action"/> under a sub-budget linked to the outer
    /// token. If the sub-budget elapses, the action is cancelled but the
    /// outer token stays alive so later stages still run. If the outer token
    /// is cancelled, the action is also cancelled and the caller can detect it.
    /// </summary>
    private static void RunWithBudget(CancellationToken outerCt, TimeSpan budget, Action<CancellationToken> action)
    {
        using var subCts  = new CancellationTokenSource(budget);
        using var linked  = CancellationTokenSource.CreateLinkedTokenSource(outerCt, subCts.Token);
        try { action(linked.Token); }
        catch (OperationCanceledException) when (subCts.IsCancellationRequested && !outerCt.IsCancellationRequested)
        {
            // Sub-budget elapsed; swallow so later stages can still run.
        }
    }

    public async Task<AnalysisResultDto> RunAsync(
        PetriNetDto                          dto,
        int                                  maxMarkings,
        IProgress<AnalysisProgressMessage>?  progress,
        CancellationToken                    ct)
    {
        var net    = PetriNetMapper.ToSnapshot(dto);
        var report = new AnalysisReport { Net = net };
        int cap    = AnalysisLimits.ClampMaxMarkings(maxMarkings);

        bool cancelled = false;

        await Task.Run(() =>
        {
            // ── Stage 1: Coverability tree (Karp-Miller) ─────────────────────
            if (ct.IsCancellationRequested) { cancelled = true; return; }
            progress?.Report(new(AnalysisProgressMessage.StageStateSpace, 5, null));

            // Special arcs (inhibitor/reset) violate monotone firing semantics that
            // Karp-Miller assumes. For those nets we run plain bounded reachability
            // with cycle detection instead — correct results, no ω verdicts.
            bool hasSpecialArcs = net.Arcs.Any(a => a.ArcType != PnArcType.Normal);
            var cb = new CoverabilityTreeBuilder();
            RunWithBudget(ct, _coverabilityBudget, subCt =>
                cb.Build(net, subCt, cap, disableOmegaAcceleration: hasSpecialArcs));
            if (!cb.HasErrors || cb.IsTruncated) report.CoverabilityTree = cb;
            if (ct.IsCancellationRequested) { cancelled = true; return; }

            bool isUnbounded = cb.Nodes.Any(n => n.Marking.Any(CoverabilityTreeBuilder.GrowsWithoutBound));

            // For special-arc nets we skipped ω-acceleration, so a truncated cb means
            // the state space is too large to compute exhaustively. Treat that the
            // same as "unbounded" downstream so we don't burn time on engines that
            // need a complete state space (and would time out on large nets).
            bool stateSpaceTooLarge = isUnbounded || (hasSpecialArcs && cb.IsTruncated);

            // ── Stage 2: State-space (skip if unbounded or too large) ─────────
            var ss = new StateSpaceAnalysis();
            RunWithBudget(ct, _stateSpaceBudget, subCt =>
                ss.Build(net, subCt, stateSpaceTooLarge ? Math.Min(500, cap) : cap));
            report.StateSpace = ss;
            if (ct.IsCancellationRequested) { cancelled = true; return; }

            progress?.Report(new(AnalysisProgressMessage.StageInvariants, 30, null));
            var inv = new InvariantAnalysis();
            // Invariants are STRUCTURAL — well-defined regardless of boundedness,
            // and T-invariant coverage in particular is a necessary condition
            // for reversibility that we want available even on unbounded nets.
            // We only skip when the net has inhibitor/reset arcs: the ordinary
            // incidence matrix doesn't capture their semantics, so the result
            // would be misleading.
            if (hasSpecialArcs)
                inv.Skip("Net contains inhibitor or reset arcs — invariants based on the ordinary incidence matrix would be misleading.");
            else
                RunWithBudget(ct, _structuralBudget, subCt => inv.Compute(net, subCt));
            report.Invariants = inv;
            if (ct.IsCancellationRequested) { cancelled = true; return; }

            progress?.Report(new(AnalysisProgressMessage.StageClassification, 50, null));
            var cls = new ClassificationAnalysis();
            RunWithBudget(ct, _structuralBudget, subCt => cls.Compute(net, subCt));
            report.Classification = cls;
            if (ct.IsCancellationRequested) { cancelled = true; return; }

            progress?.Report(new(AnalysisProgressMessage.StageCycles, 65, null));
            var cyc = new CyclesAnalysis();
            RunWithBudget(ct, _structuralBudget, subCt => cyc.Compute(net, subCt));
            report.Cycles = cyc;
            var tc = new TrapCotrapAnalysis();
            RunWithBudget(ct, _structuralBudget, subCt => tc.Compute(net, subCt));
            report.TrapCotrap = tc;
            if (ct.IsCancellationRequested) { cancelled = true; return; }

            progress?.Report(new(AnalysisProgressMessage.StagePropertyTests, 70, null));
            var bundle = new AnalysisBundle
            {
                Net              = net,
                StateSpace       = ss,
                CoverabilityTree = cb,   // lets property tests see ω-nodes / cb-deadlocks
                Invariants       = inv,
                Classification   = cls,
                Cycles           = cyc,
                TrapCotrap       = tc,
            };

            var results = bundle.PropertyResults;
            // Property tests run under a shared sub-budget. They short-circuit on
            // cancellation between tests so a slow earlier test doesn't starve later ones.
            using (var propCts = new CancellationTokenSource(_propertyBudget))
            using (var propLinked = CancellationTokenSource.CreateLinkedTokenSource(ct, propCts.Token))
            {
                var propCt = propLinked.Token;
                results[NetProperty.Liveness]         = SafeRun(NetProperty.Liveness,         propCt, () => new LivenessTest().Run(bundle));
                results[NetProperty.Boundedness]      = SafeRun(NetProperty.Boundedness,      propCt, () => new BoundednessTest().Run(bundle));
                results[NetProperty.Safety]           = SafeRun(NetProperty.Safety,           propCt, () => new SafetyTest().Run(bundle));
                results[NetProperty.Conservativeness]       = SafeRun(NetProperty.Conservativeness,       propCt, () => new ConservativenessTest().Run(bundle));
                results[NetProperty.StrictConservativeness] = SafeRun(NetProperty.StrictConservativeness, propCt, () => new StrictConservativenessTest().Run(bundle));
                results[NetProperty.Repetitiveness]         = SafeRun(NetProperty.Repetitiveness,         propCt, () => new RepetitivenessTest().Run(bundle));
                results[NetProperty.DeadlockFree]     = SafeRun(NetProperty.DeadlockFree,     propCt, () => new DeadlockFreeTest().Run(bundle));
                results[NetProperty.Reversibility]    = SafeRun(NetProperty.Reversibility,    propCt, () => new ReversibilityTest().Run(bundle));
            }
            report.PropertyResults = results;
            if (ct.IsCancellationRequested) { cancelled = true; return; }

            progress?.Report(new(AnalysisProgressMessage.StagePropertyTests, 90, null));

            if (!stateSpaceTooLarge)
            {
                var rt = new ReachabilityTreeBuilder();
                RunWithBudget(ct, _stateSpaceBudget, subCt =>
                    rt.Build(net, subCt, cap));
                if (!rt.HasErrors || rt.IsTruncated) report.ReachabilityTree = rt;
            }
        });

        if (cancelled)
            throw new OperationCanceledException(ct);

        return AnalysisResultMapper.ToDto(report);
    }

    public async Task<GraphResultDto> RunGraphAsync(
        PetriNetDto       dto,
        int               maxMarkings,
        CancellationToken ct)
    {
        var net = PetriNetMapper.ToSnapshot(dto);
        int cap = AnalysisLimits.ClampMaxMarkings(maxMarkings);

        CoverabilityTreeDto?  coverDto = null;
        StateSpaceSummaryDto? ssDto    = null;
        string? error = null;

        await Task.Run(() =>
        {
            try
            {
                // Always use the coverability tree — it handles both bounded and unbounded nets.
                // Inhibitor/reset arcs disable ω-acceleration so the result stays correct.
                bool hasSpecialArcs = net.Arcs.Any(a => a.ArcType != PnArcType.Normal);
                var cb = new CoverabilityTreeBuilder();
                RunWithBudget(ct, _coverabilityBudget, subCt =>
                    cb.Build(net, subCt, cap, disableOmegaAcceleration: hasSpecialArcs));
                // Genuine failure (no usable tree): surface as ErrorMessage.
                if (cb.HasErrors && !cb.IsTruncated) { error = cb.ErrorMessage; return; }
                // Truncation is reflected via StateSpaceSummaryDto.ExceededLimit, not ErrorMessage.

                coverDto = AnalysisResultMapper.BuildCoverabilityTreeDto(net, cb);

                bool isUnbounded = cb.Nodes.Any(n => n.Marking.Any(CoverabilityTreeBuilder.GrowsWithoutBound));
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

    private PropertyTestResult SafeRun(NetProperty property, CancellationToken ct, Func<PropertyTestResult> action)
    {
        if (ct.IsCancellationRequested)
            return new PropertyTestResult(
                property,
                TestResultStatus.Undecidable,
                [$"{property} test skipped: analysis time budget exceeded."],
                []);
        try
        {
            return action();
        }
        catch (OperationCanceledException)
        {
            return new PropertyTestResult(
                property,
                TestResultStatus.Undecidable,
                [$"{property} test cancelled: analysis time budget exceeded."],
                []);
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
