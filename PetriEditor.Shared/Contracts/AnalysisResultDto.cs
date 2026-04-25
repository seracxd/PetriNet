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
    IReadOnlyList<string>              ClassificationSubclasses,
    IReadOnlyList<PropertyResultDto>   PropertyResults,
    IReadOnlyList<InvariantDto>        PInvariants,
    IReadOnlyList<InvariantDto>        TInvariants,
    bool                               InvariantsTruncated,
    ReachabilityGraphDto?              ReachabilityGraph,
    ReachabilityGraphDto?              ReachabilityTree,
    CoverabilityTreeDto?               CoverabilityTree,
    CyclesDto?                         Cycles,
    TrapsDto?                          Traps,
    NetStructureDto                    NetStructure);

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

// ── On-demand graph result ────────────────────────────────────────────────────

/// <summary>Result of a lazy graph/tree computation requested separately from the main analysis.</summary>
public sealed record GraphResultDto(
    ReachabilityGraphDto?    ReachabilityGraph,
    ReachabilityGraphDto?    ReachabilityTree,
    CoverabilityTreeDto?     CoverabilityTree,
    string?                  ErrorMessage,
    StateSpaceSummaryDto?    StateSpace = null);

/// <summary>Lightweight state-space facts derived during graph computation.</summary>
public sealed record StateSpaceSummaryDto(
    int  StateCount,
    bool IsBounded,
    bool IsSafe,
    bool IsDeadlockFree,
    bool IsReversible,
    bool ExceededLimit);

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
    bool                 IsTruncated,
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
    IReadOnlyList<string>       PlaceNames,
    GraphLayoutDto?             GraphLayout = null);

/// <summary>
/// Pre-computed layered layout for the reachability/coverability graph view.
/// Duplicate-marking nodes are merged; each surviving node has an integer
/// <c>Layer</c> (0 = root, increasing downward) and <c>Col</c> (left→right
/// order within the layer).  Edges that close a cycle (target layer ≤ source
/// layer) are flagged as back-edges so the renderer can route them distinctly.
/// </summary>
public sealed record GraphLayoutDto(
    IReadOnlyList<GraphLayoutNodeDto> Nodes,
    IReadOnlyList<GraphLayoutEdgeDto> Edges,
    int                               MaxLayer,
    int                               MaxCol);

public sealed record GraphLayoutNodeDto(
    int                 Id,
    int                 Layer,
    int                 Col,
    string              Label,
    string              MarkingKey,
    IReadOnlyList<int?> Marking,
    bool                IsInitial,
    bool                IsDeadlock,
    bool                IsOmega,
    bool                IsTruncated);

public sealed record GraphLayoutEdgeDto(
    int    From,
    int    To,
    string TransitionName,
    int    FromLayer,
    int    ToLayer,
    bool   IsBack,
    bool   IsSelf);

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
    bool                  IsTruncated,
    int                   ParentId);

/// <summary>A directed edge between two coverability tree nodes.</summary>
public sealed record CoverEdgeDto(
    int    From,
    int    To,
    string TransitionId,
    string TransitionName);

// ── Cycles ────────────────────────────────────────────────────────────────────

public sealed record CyclesDto(
    bool                        HasErrors,
    string?                     ErrorMsg,
    IReadOnlyList<CycleDto>     Cycles,
    int                         PlaceCoverage,
    int                         TransitionCoverage);

public sealed record CycleDto(
    IReadOnlyList<string>   NodeIds,
    IReadOnlyList<string>   PlaceIds,
    IReadOnlyList<string>   TransitionIds,
    int                     TokensInCycle);

// ── Traps ─────────────────────────────────────────────────────────────────────

public sealed record TrapsDto(
    bool                          HasErrors,
    string?                       ErrorMsg,
    IReadOnlyList<PlaceSubsetDto> Traps,
    IReadOnlyList<PlaceSubsetDto> Siphons);

public sealed record PlaceSubsetDto(
    IReadOnlyList<string> PlaceIds,
    bool                  HasToken);

// ── Streaming graph chunk ─────────────────────────────────────────────────────

/// <summary>
/// One chunk of graph/tree data streamed incrementally to the client.
/// GraphKey: "rg" = reachability graph, "rt" = reachability tree, "ct" = coverability tree.
/// PlaceNames and Summary are populated only on the first chunk (ChunkIndex == 0).
/// Node chunks carry ReachNodes or CoverNodes; the final chunk (ChunkIndex == TotalChunks-1)
/// carries edges and has null node lists.
/// </summary>
public sealed record GraphChunkDto(
    string                       GraphKey,
    int                          ChunkIndex,
    int                          TotalChunks,
    IReadOnlyList<ReachNodeDto>? ReachNodes,
    IReadOnlyList<ReachEdgeDto>? ReachEdges,
    IReadOnlyList<CoverNodeDto>? CoverNodes,
    IReadOnlyList<CoverEdgeDto>? CoverEdges,
    IReadOnlyList<string>?       PlaceNames,
    StateSpaceSummaryDto?        Summary,
    GraphLayoutDto?              GraphLayout = null);

// ── Net structure ─────────────────────────────────────────────────────────────

public sealed record NetStructureDto(
    int PlaceCount,
    int TransitionCount,
    int NormalArcCount,
    int InhibitorArcCount,
    int ResetArcCount,
    int InitialTokenCount);
