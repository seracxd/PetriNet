namespace PetriEditor.Shared.Contracts;

/// <summary>
/// Streamed from server to client over SignalR during a long analysis run.
/// The client uses Stage and Percent to update a progress bar.
/// </summary>
public sealed record AnalysisProgressMessage(
    string  Stage,
    int     Percent,
    string? ErrorText)
{
    // Known stage names — listed here so UI code can match against them.
    public const string StageStateSpace       = "StateSpace";
    public const string StageInvariants       = "Invariants";
    public const string StageClassification   = "Classification";
    public const string StageCycles           = "Cycles";
    public const string StageReachTree        = "ReachabilityTree";
    public const string StageCoverTree        = "CoverabilityTree";
    public const string StagePropertyTests    = "PropertyTests";
    public const string StageComplete         = "Complete";
    public const string StageError            = "Error";
}
