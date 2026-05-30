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
    StrictConservativeness,
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

/// <summary>
/// Builder used by property tests to accumulate reasons and set final status.
/// Each reason is tagged with the status of the branch that produced it.
/// On <see cref="Build"/>, only the reasons relevant to the final verdict are kept:
/// <list type="bullet">
///   <item>Final Pass/Fail → keep only Pass/Fail reasons (drop all Undecidable noise).</item>
///   <item>Final Undecidable → keep only the first reason that explains the best partial
///         result (Fail > Undecidable), so the user sees why we couldn't decide.</item>
/// </list>
/// </summary>
public sealed class PropertyResultBuilder(NetProperty property)
{
    private readonly NetProperty  _property = property;
    private TestResultStatus      _status   = TestResultStatus.Undecidable;
    private readonly List<(TestResultStatus BranchStatus, string Text)> _reasons = [];
    private readonly List<string> _errors   = [];

    public void SetStatus(TestResultStatus s) => _status = s;

    /// <summary>Record a reason tagged with the status of the branch that produced it.</summary>
    public void AddReason(string r, TestResultStatus branchStatus) =>
        _reasons.Add((branchStatus, r));

    /// <summary>Convenience overload — caller passes status explicitly.</summary>
    public void AddReason(string r) => AddReason(r, TestResultStatus.Undecidable);

    public void LogError(string e) => _errors.Add(e);

    public PropertyTestResult Build()
    {
        IEnumerable<string> filtered;

        if (_status == TestResultStatus.Pass || _status == TestResultStatus.Fail)
        {
            // Only keep reasons from branches that produced the same decisive result.
            filtered = _reasons
                .Where(r => r.BranchStatus == _status)
                .Select(r => r.Text);
        }
        else
        {
            // Undecidable overall: prefer Fail reasons (partial evidence) over pure
            // Undecidable ones, but if there are none, fall back to the first Undecidable.
            var failReasons = _reasons.Where(r => r.BranchStatus == TestResultStatus.Fail).ToList();
            filtered = failReasons.Count > 0
                ? failReasons.Select(r => r.Text)
                : _reasons.Take(1).Select(r => r.Text);
        }

        return new(_property, _status, filtered, _errors);
    }
}

