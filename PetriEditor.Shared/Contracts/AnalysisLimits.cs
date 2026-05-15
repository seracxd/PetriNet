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
    /// <summary>
    /// Absolute ceiling on the user-selectable marking cap. The slider in the
    /// UI cannot exceed this. Raising it lets users explore larger nets but
    /// trades off browser memory and latency on the WASM client.
    /// </summary>
    public const int MaxMarkings = 100_000;

    /// <summary>
    /// Soft memory ceiling for BFS-style builders, in bytes. When managed heap
    /// usage exceeds this during a build, the builder stops and marks the
    /// result as truncated rather than risking OutOfMemoryException. Acts as
    /// a safety net independent of the user-selected marking cap.
    /// </summary>
    public const long MaxBuildHeapBytes = 1_073_741_824L; // 1 GB

    /// <summary>How often (in BFS iterations) to sample heap usage.</summary>
    public const int HeapSampleInterval = 1_000;

    /// <summary>Clamp the user-supplied cap into the supported range.</summary>
    public static int ClampMaxMarkings(int requested) =>
        requested <= 0 ? MaxMarkings : Math.Min(requested, MaxMarkings);
}
