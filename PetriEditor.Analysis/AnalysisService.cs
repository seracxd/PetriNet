using Analysis.Algorithms;
using Analysis.Engines;

namespace Analysis;

/// <summary>
/// Orchestrates all analysis engines and property tests.
/// Register as scoped: builder.Services.AddScoped&lt;AnalysisService&gt;();
/// </summary>
public sealed class AnalysisService
{
    public async Task<AnalysisReport> RunAsync(
        PetriNetSnapshot net,
        CancellationToken ct = default)
    {
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
            ct.ThrowIfCancellationRequested();

            var tc = new TrapCotrapAnalysis();
            tc.Compute(net);
            report.TrapCotrap = tc;
            ct.ThrowIfCancellationRequested();

            var bundle = new AnalysisBundle
            {
                Net = net,
                StateSpace = ss,
                Invariants = inv,
                Classification = cls,
                Cycles = cyc,
                TrapCotrap = tc,
            };

            var results = bundle.PropertyResults;
            results[NetProperty.Liveness] = SafeRun(NetProperty.Liveness, () => new LivenessTest().Run(bundle));
            results[NetProperty.Boundedness] = SafeRun(NetProperty.Boundedness, () => new BoundednessTest().Run(bundle));
            results[NetProperty.Safety] = SafeRun(NetProperty.Safety, () => new SafetyTest().Run(bundle));
            results[NetProperty.Conservativeness] = SafeRun(NetProperty.Conservativeness, () => new ConservativenessTest().Run(bundle));
            results[NetProperty.Repetitiveness] = SafeRun(NetProperty.Repetitiveness, () => new RepetitivenessTest().Run(bundle));
            results[NetProperty.DeadlockFree] = SafeRun(NetProperty.DeadlockFree, () => new DeadlockFreeTest().Run(bundle));
            results[NetProperty.Reversibility] = SafeRun(NetProperty.Reversibility, () => new ReversibilityTest().Run(bundle));
            report.PropertyResults = results;
        }, ct);

        return report;
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
                [
                    $"{GetPropertyLabel(property)} could not be completed because the current algorithm hit an internal error.",
                    "This result is currently unavailable for this net.",
                    "To fix the root cause, the corresponding property-test implementation needs to be checked."
                ],
                [$"{ToFriendlyErrorMessage(ex)}"]);
        }
    }

    private static string GetPropertyLabel(NetProperty property) => property switch
    {
        NetProperty.Liveness => "Liveness",
        NetProperty.Boundedness => "Boundedness",
        NetProperty.Safety => "Safety",
        NetProperty.Conservativeness => "Conservativeness",
        NetProperty.Repetitiveness => "Repetitiveness",
        NetProperty.DeadlockFree => "Deadlock-freedom",
        NetProperty.Reversibility => "Reversibility",
        _ => property.ToString()
    };

    private static string ToFriendlyErrorMessage(Exception ex)
    {
        return ex switch
        {
            IndexOutOfRangeException => "Internal error in the analysis algorithm: it tried to read a state, marking, or matrix position outside the available range.",
            ArgumentOutOfRangeException => "Internal error in the analysis algorithm: it tried to access a collection item outside the valid range.",
            _ => $"Internal error: {ex.GetType().Name}: {ex.Message}"
        };
    }

}

public sealed class AnalysisReport
{
    public PetriNetSnapshot Net { get; init; } = null!;

    public StateSpaceAnalysis? StateSpace { get; set; }
    public InvariantAnalysis? Invariants { get; set; }
    public ClassificationAnalysis? Classification { get; set; }
    public CyclesAnalysis? Cycles { get; set; }
    public TrapCotrapAnalysis? TrapCotrap { get; set; }
    public ReachabilityTreeBuilder? ReachabilityTree { get; set; }
    public CoverabilityTreeBuilder? CoverabilityTree { get; set; }

    public Dictionary<NetProperty, PropertyTestResult> PropertyResults { get; set; } = [];

    public PropertyTestResult? Get(NetProperty property) =>
        PropertyResults.GetValueOrDefault(property);

    public string ClassificationSummary =>
        Classification?.HasErrors == false ? Classification.Summary() : "N/A";

    public int StateCount => StateSpace?.States.Count ?? 0;

    public string ResolveName(string id)
    {
        if (Net.PlaceById.TryGetValue(id, out var place))
            return place.Name;

        if (Net.TransitionById.TryGetValue(id, out var transition))
            return transition.Name;

        return id;
    }

    public IReadOnlyList<string> UnboundedPlaceIds()
    {
        if (StateSpace?.States == null || StateSpace.States.Count == 0)
            return [];

        var result = new List<string>();

        for (var i = 0; i < Net.Places.Count; i++)
        {
            if (StateSpace.States.Any(state => state[i] == -1))
                result.Add(Net.Places[i].Id);
        }

        return result;
    }
}
