using Core.Models;

namespace PetriEditor.Shared.Contracts;

/// <summary>
/// Wire representation of a full Petri net sent between client and server.
/// Positions (X, Y) are included so TikZ export and diagram restoration
/// can place nodes at the same coordinates.
/// </summary>
public sealed record PetriNetDto(
    IReadOnlyList<PlaceDto>      Places,
    IReadOnlyList<TransitionDto> Transitions,
    IReadOnlyList<ArcDto>        Arcs);

public sealed record PlaceDto(
    string Id,
    string Name,
    int    Tokens,
    double X,
    double Y);

public sealed record TransitionDto(
    string Id,
    string Name,
    int    Priority,
    double X,
    double Y);

public sealed record ArcDto(
    string                  SourceId,
    string                  TargetId,
    int                     Weight,
    ArcType                 ArcType,
    IReadOnlyList<PointDto> Vertices);

/// <summary>A 2-D coordinate used for arc bend points and node positions.</summary>
public sealed record PointDto(double X, double Y);
