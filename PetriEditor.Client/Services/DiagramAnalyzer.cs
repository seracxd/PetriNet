using Blazor.Diagrams.Core.Models;
using Core.Models;
using PetriNetAnalyzer.DiagramModels;
using PetriEditor.Shared.Contracts;

namespace PetriEditor.Client.Services;

/// <summary>
/// Builds a <see cref="PetriNetDto"/> from the current diagram state.  The
/// DTO is what gets sent to the server for analysis.
/// </summary>
public sealed class DiagramAnalyzer
{
    public static PetriNetDto BuildDto(
        IEnumerable<PlaceNode>      places,
        IEnumerable<TransitionNode> transitions,
        IEnumerable<PetriLinkModel> arcs)
    {
        var validIds = new HashSet<string>();
        var placeDtos = places.Select(p =>
        {
            validIds.Add(p.Data.Id);
            return new PlaceDto(p.Data.Id, p.Data.Name, p.Data.Tokens, p.Position.X, p.Position.Y);
        }).ToList();

        var transitionDtos = transitions.Select(t =>
        {
            validIds.Add(t.Data.Id);
            return new TransitionDto(t.Data.Id, t.Data.Name, t.Data.Priority, t.Position.X, t.Position.Y);
        }).ToList();

        var arcDtos = arcs
            .Where(a => !a.IsDraggingEndpoint && a.Target != null)
            .Select(a =>
            {
                var sourceId = GetNodeId(a.Source?.Model);
                var targetId = GetNodeId(a.Target?.Model);

                if (sourceId is null || targetId is null)
                    return null;

                if (!validIds.Contains(sourceId) || !validIds.Contains(targetId))
                    return null;

                var vertices = (a.Vertices ?? [])
                    .Select(v => new PointDto(v.Position.X, v.Position.Y))
                    .ToList();

                return new ArcDto(sourceId, targetId, Math.Max(1, a.Weight), a.ArcType, vertices);
            })
            .OfType<ArcDto>()
            .ToList();

        return new PetriNetDto(placeDtos, transitionDtos, arcDtos);
    }

    private static string? GetNodeId(object? model) => model switch
    {
        PlaceNode p      => p.Data.Id,
        TransitionNode t => t.Data.Id,
        PortModel port   => port.Parent switch
        {
            PlaceNode p      => p.Data.Id,
            TransitionNode t => t.Data.Id,
            _                => null
        },
        _ => null
    };
}
