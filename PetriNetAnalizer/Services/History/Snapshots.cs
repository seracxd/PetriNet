using Blazor.Diagrams.Core.Geometry;
using Core.Models;

namespace PetriNetAnalyzer.Services.History;

/// <summary>Immutable snapshot of a Place node, capturing everything needed to recreate it.</summary>
public sealed record PlaceSnapshot(
    string Id,
    string Name,
    int Tokens,
    double X,
    double Y);

/// <summary>Immutable snapshot of a Transition node, capturing everything needed to recreate it.</summary>
public sealed record TransitionSnapshot(
    string Id,
    string Name,
    int Priority,
    double X,
    double Y);

/// <summary>
/// Immutable snapshot of a directed arc.
///
/// <b>SourceNodeId / TargetNodeId are domain IDs</b> (i.e. <c>Place.Id</c> /
/// <c>Transition.Id</c>), <em>not</em> Blazor.Diagrams' internal <c>NodeModel.Id</c>
/// GUIDs. The domain ID is the only stable identifier across undo/redo cycles that
/// recreate fresh <c>NodeModel</c> instances.
/// </summary>
public sealed record LinkSnapshot(
    string SourceNodeId,
    string TargetNodeId,
    int Weight,
    ArcType ArcType,
    IReadOnlyList<Point> VertexPositions,
    int WeightLabelSegment,
    bool WeightLabelFlipped);
