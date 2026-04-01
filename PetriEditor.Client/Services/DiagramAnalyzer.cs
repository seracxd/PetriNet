using Analysis;
using Analysis.Algorithms;
using Analysis.Engines;
using Blazor.Diagrams.Core.Models;
using Core.Models;
using PetriNetAnalyzer.DiagramModels;
using PetriEditor.Shared.Contracts;
using PetriEditor.Shared.Mapping;

namespace PetriEditor.Client.Services;

/// <summary>
/// Builds a <see cref="PetriNetDto"/> from the current diagram state and runs
/// a full analysis locally, returning the engine-level <see cref="AnalysisReport"/>.
///
/// Used by <see cref="PetriEditor.Client.Components.AnalysisPanel"/> to populate
/// the rich UI that still references engine types directly (Classification,
/// Cycles, Traps, etc.). Migration to DTO-only rendering is deferred to Phase 6.
///
/// Also builds the <see cref="PetriNetDto"/> used when sending a net to the server.
/// </summary>
public sealed class DiagramAnalyzer
{
    // ── Build DTO from diagram state ─────────────────────────────────────

    /// <summary>
    /// Extract the current diagram state into a <see cref="PetriNetDto"/> that
    /// can be sent over the wire or converted to a <see cref="PetriNetSnapshot"/>.
    /// </summary>
    public static PetriNetDto BuildDto(
        IEnumerable<PlaceNode>      places,
        IEnumerable<TransitionNode> transitions,
        IEnumerable<PetriLinkModel> arcs)
    {
        var validIds = new HashSet<string>();
        var placeDtos = places.Select(p =>
        {
            validIds.Add(p.Data.Id);
            return new PlaceDto(p.Data.Id, p.Data.Name, p.Data.Tokens, p.Position.X, p.Position.Y);
        }).ToList();

        var transitionDtos = transitions.Select(t =>
        {
            validIds.Add(t.Data.Id);
            return new TransitionDto(t.Data.Id, t.Data.Name, t.Data.Priority, t.Position.X, t.Position.Y);
        }).ToList();

        var arcDtos = arcs
            .Where(a => !a.IsDraggingEndpoint && a.Target != null)
            .Select(a =>
            {
                var sourceId = GetNodeId(a.Source?.Model);
                var targetId = GetNodeId(a.Target?.Model);

                if (sourceId is null || targetId is null)
                    return null;

                if (!validIds.Contains(sourceId) || !validIds.Contains(targetId))
                    return null;

                var vertices = (a.Vertices ?? [])
                    .Select(v => new PointDto(v.Position.X, v.Position.Y))
                    .ToList();

                return new ArcDto(sourceId, targetId, Math.Max(1, a.Weight), a.ArcType, vertices);
            })
            .OfType<ArcDto>()
            .ToList();

        return new PetriNetDto(placeDtos, transitionDtos, arcDtos);
    }

    // ── Run analysis locally, returning the engine report ─────────────────

    public async Task<AnalysisReport> RunLocalAsync(
        PetriNetDto           dto,
        CancellationToken     ct = default)
    {
        var net    = PetriNetMapper.ToSnapshot(dto);
        var report = new AnalysisReport { Net = net };

        await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            var ss = new StateSpaceAnalysis();
            ss.Build(net);
            report.StateSpace = ss;

            ct.ThrowIfCancellationRequested();
            var inv = new InvariantAnalysis();
            inv.Compute(net);
            report.Invariants = inv;

            ct.ThrowIfCancellationRequested();
            var cls = new ClassificationAnalysis();
            cls.Compute(net);
            report.Classification = cls;

            ct.ThrowIfCancellationRequested();
            var cyc = new CyclesAnalysis();
            cyc.Compute(net);
            report.Cycles = cyc;

            var tc = new TrapCotrapAnalysis();
            tc.Compute(net);
            report.TrapCotrap = tc;

            ct.ThrowIfCancellationRequested();
            var rt = new ReachabilityTreeBuilder();
            rt.Build(net, ct);
            report.ReachabilityTree = rt;

            ct.ThrowIfCancellationRequested();
            var coverBuilder = new CoverabilityTreeBuilder();
            coverBuilder.Build(net, ct);
            report.CoverabilityTree = coverBuilder;

            ct.ThrowIfCancellationRequested();
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
        }, ct);

        return report;
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static string? GetNodeId(object? model) => model switch
    {
        PlaceNode p      => p.Data.Id,
        TransitionNode t => t.Data.Id,
        PortModel port   => port.Parent switch
        {
            PlaceNode p      => p.Data.Id,
            TransitionNode t => t.Data.Id,
            _                => null
        },
        _ => null
    };

    private static PropertyTestResult SafeRun(NetProperty property, Func<PropertyTestResult> action)
    {
        try { return action(); }
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
