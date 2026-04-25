namespace PetriEditor.Shared.Contracts;

/// <summary>
/// Single source of truth for analysis size limits.
///
/// <see cref="MaxMarkings"/> is the hard cap on how many distinct markings any
/// engine will explore before truncating.  Every state-space / reachability /
/// coverability builder honours this cap.  It is independent of what the client
/// chooses to display — that is a separate UI-side concern.
///
/// Raising this value trades off latency and memory for larger nets.  The
/// per-request deadline in the hub is the ultimate safety net.
/// </summary>
public static class AnalysisLimits
{
    /// <summary>Maximum markings computed by any engine.</summary>
    public const int MaxMarkings = 100_000;
}
