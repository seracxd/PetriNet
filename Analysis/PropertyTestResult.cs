namespace Analysis;

public enum TestResultStatus
{
    /// <summary>Not enough information to decide.</summary>
    Undecidable,
    /// <summary>Property holds.</summary>
    Pass,
    /// <summary>Property does not hold.</summary>
    Fail
}

public static class TestResultStatusExtensions
{
    /// <summary>
    /// Returns the strongest result across all inputs: Pass > Fail > Undecidable.
    /// Mirrors the Java logicalOr: first definitive answer wins.
    /// </summary>
    public static TestResultStatus LogicalOr(params TestResultStatus[] statuses)
    {
        var best = TestResultStatus.Undecidable;
        foreach (var s in statuses)
        {
            if (s == TestResultStatus.Pass) return TestResultStatus.Pass;
            if (s == TestResultStatus.Fail) best = TestResultStatus.Fail;
        }
        return best;
    }
}

public enum NetProperty
{
    Liveness,
    Boundedness,
    Safety,
    Conservativeness,
    Repetitiveness,
    DeadlockFree,
    Reversibility
}

public sealed class PropertyTestResult
{
    public NetProperty           Property { get; }
    public TestResultStatus      Status   { get; }
    public IReadOnlyList<string> Reasons  { get; }
    public IReadOnlyList<string> Errors   { get; }

    public PropertyTestResult(NetProperty property, TestResultStatus status,
        IEnumerable<string> reasons, IEnumerable<string> errors)
    {
        Property = property;
        Status   = status;
        Reasons  = reasons.ToList();
        Errors   = errors.ToList();
    }

    public string StatusLabel => Status switch
    {
        TestResultStatus.Pass        => "✓ Pass",
        TestResultStatus.Fail        => "✗ Fail",
        TestResultStatus.Undecidable => "? Undecidable",
        _                            => "?"
    };

    public string StatusColor => Status switch
    {
        TestResultStatus.Pass        => "#2e7d32",
        TestResultStatus.Fail        => "#c62828",
        TestResultStatus.Undecidable => "#f57f17",
        _                            => "#888"
    };

    public string StatusBackground => Status switch
    {
        TestResultStatus.Pass        => "#e8f5e9",
        TestResultStatus.Fail        => "#ffebee",
        TestResultStatus.Undecidable => "#fff8e1",
        _                            => "#f5f5f5"
    };
}

/// <summary>Builder used internally by property tests.</summary>
internal sealed class PropertyResultBuilder(NetProperty property)
{
    private readonly NetProperty  _property = property;
    private TestResultStatus      _status   = TestResultStatus.Undecidable;
    private readonly List<string> _reasons  = [];
    private readonly List<string> _errors   = [];

    public void SetStatus(TestResultStatus s) => _status = s;
    public void AddReason(string r)           => _reasons.Add(r);
    public void LogError(string e)            => _errors.Add(e);

    public PropertyTestResult Build() =>
        new(_property, _status, _reasons, _errors);
}

/// <summary>All engine outputs passed to every property test.</summary>
public sealed class AnalysisBundle
{
    public PetriNetSnapshot Net { get; init; } = null!;

    public Analysis.Engines.StateSpaceAnalysis?    StateSpace     { get; init; }
    public Analysis.Engines.InvariantAnalysis?     Invariants     { get; init; }
    public Analysis.Engines.ClassificationAnalysis? Classification { get; init; }
    public Analysis.Engines.CyclesAnalysis?        Cycles         { get; init; }
    public Analysis.Engines.TrapCotrapAnalysis?    TrapCotrap     { get; init; }

    /// Results of other property tests (used by DeadlockFree which needs Liveness).
    public Dictionary<NetProperty, PropertyTestResult> PropertyResults { get; } = [];
}
