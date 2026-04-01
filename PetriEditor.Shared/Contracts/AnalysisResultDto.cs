namespace PetriEditor.Shared.Contracts;

/// <summary>
/// Full analysis result returned by the server after RunAnalysisAsync completes.
/// All fields are plain data — no references to Analysis engine types.
/// </summary>
public sealed record AnalysisResultDto(
    int                                StateCount,
    bool                               IsBounded,
    bool                               IsDeadlockFree,
    bool                               IsReversible,
    bool                               IsSafe,
    bool                               IsLive,
    string                             ClassificationSummary,
    IReadOnlyList<PropertyResultDto>   PropertyResults,
    IReadOnlyList<InvariantDto>        PInvariants,
    IReadOnlyList<InvariantDto>        TInvariants,
    ReachabilityGraphDto?              ReachabilityGraph,
    CoverabilityTreeDto?               CoverabilityTree);

/// <summary>Result of a single property test (liveness, safety, etc.).</summary>
public sealed record PropertyResultDto(
    string                    Property,
    string                    Status,
    string                    StatusLabel,
    string                    StatusColor,
    string                    StatusBackground,
    IReadOnlyList<string>     Reasons,
    IReadOnlyList<string>     Errors);

/// <summary>A single P- or T-invariant: maps place/transition ID to coefficient.</summary>
public sealed record InvariantDto(
    IReadOnlyDictionary<string, int> Structure);

// ── Reachability graph ────────────────────────────────────────────────────────

/// <summary>
/// The reachability graph — all distinct markings reachable from the initial
/// marking, with directed edges labelled by transition names.
/// </summary>
public sealed record ReachabilityGraphDto(
    IReadOnlyList<ReachNodeDto> Nodes,
    IReadOnlyList<ReachEdgeDto> Edges,
    IReadOnlyList<string>       PlaceNames);

/// <summary>A single node in the reachability graph.</summary>
public sealed record ReachNodeDto(
    int                  Id,
    IReadOnlyList<int>   Marking,
    bool                 IsInitial,
    bool                 IsDeadlock,
    bool                 IsDuplicate,
    int                  ParentId);   // -1 for the root node

/// <summary>A directed edge between two reachability graph nodes.</summary>
public sealed record ReachEdgeDto(
    int    From,
    int    To,
    string TransitionId,
    string TransitionName);

// ── Coverability tree ─────────────────────────────────────────────────────────

/// <summary>
/// The coverability (Karp-Miller) tree — handles unbounded nets by replacing
/// unbounded token counts with omega (represented as null in the marking list).
/// </summary>
public sealed record CoverabilityTreeDto(
    IReadOnlyList<CoverNodeDto> Nodes,
    IReadOnlyList<CoverEdgeDto> Edges,
    IReadOnlyList<string>       PlaceNames);

/// <summary>
/// A single node in the coverability tree.
/// Marking entries are null where the token count is omega (ω).
/// </summary>
public sealed record CoverNodeDto(
    int                   Id,
    IReadOnlyList<int?>   Marking,
    bool                  IsInitial,
    bool                  IsDeadlock,
    bool                  IsDuplicate,
    int                   ParentId);

/// <summary>A directed edge between two coverability tree nodes.</summary>
public sealed record CoverEdgeDto(
    int    From,
    int    To,
    string TransitionId,
    string TransitionName);
