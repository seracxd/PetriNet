using Blazor.Diagrams.Core.Geometry;
using Blazor.Diagrams.Core.Models;
using Blazor.Diagrams.Core.Models.Base;
using Core.Models;
using PetriEditor.Shared.Contracts;
using PetriNetAnalyzer.DiagramModels;
using PetriNetAnalyzer.Services;

namespace PetriEditor.Client.Services;

/// <summary>
/// Captures the live diagram into a <see cref="PetriNetDto"/> and restores it from one.
///
/// Capture preserves: node positions, token counts, priorities, arc weights, arc types,
/// and arc bend-point vertices.
///
/// Restore clears the diagram and undo/redo history, then rebuilds from the DTO.
/// The result is structurally identical but treated as a clean starting state.
/// </summary>
public sealed class DiagramSerializer(PetriNetManager manager, DiagramSettings settings)
{
    public PetriNetDto Capture()
    {
        var places = manager.Diagram.Nodes
            .OfType<PlaceNode>()
            .Select(n => new PlaceDto(
                n.Data.Id,
                n.Data.Name,
                n.Data.Tokens,
                n.Position?.X ?? 0,
                n.Position?.Y ?? 0))
            .ToList();

        var transitions = manager.Diagram.Nodes
            .OfType<TransitionNode>()
            .Select(n => new TransitionDto(
                n.Data.Id,
                n.Data.Name,
                n.Data.Priority,
                n.Position?.X ?? 0,
                n.Position?.Y ?? 0))
            .ToList();

        var arcs = manager.Diagram.Links
            .OfType<PetriLinkModel>()
            .Where(l => l.Target is not null && l.CanonicalSourceId is not null)
            .Select(l =>
            {
                var srcId = GetNodeId(l.Source?.Model as Model);
                var tgtId = GetNodeId(l.Target?.Model as Model);
                if (srcId is null || tgtId is null) return null;
                var vertices = l.Vertices
                    .Select(v => new PointDto(v.Position?.X ?? 0, v.Position?.Y ?? 0))
                    .ToList();
                return new ArcDto(srcId, tgtId, l.Weight, (ArcType)l.ArcType, vertices);
            })
            .Where(a => a is not null)
            .Select(a => a!)
            .ToList();

        return new PetriNetDto(places, transitions, arcs);
    }

    public void Restore(PetriNetDto dto)
    {
        manager.Diagram.Nodes.Clear();
        manager.Diagram.Links.Clear();
        manager.History.Clear();

        var nodeMap = new Dictionary<string, NodeModel>();

        foreach (var p in dto.Places)
        {
            var place = new Place { Id = p.Id, Name = p.Name, Tokens = p.Tokens };
            var node = new PlaceNode(place, settings);
            node.SetPosition(p.X, p.Y);
            manager.Diagram.Nodes.Add(node);
            nodeMap[p.Id] = node;
        }

        foreach (var t in dto.Transitions)
        {
            var transition = new Transition { Id = t.Id, Name = t.Name, Priority = t.Priority };
            var node = new TransitionNode(transition, settings);
            node.SetPosition(t.X, t.Y);
            manager.Diagram.Nodes.Add(node);
            nodeMap[t.Id] = node;
        }

        foreach (var a in dto.Arcs)
        {
            if (!nodeMap.TryGetValue(a.SourceId, out var src)) continue;
            if (!nodeMap.TryGetValue(a.TargetId, out var tgt)) continue;
            var vertices = a.Vertices.Select(v => new Point(v.X, v.Y));
            manager.RestoreLink(src, tgt, a.Weight, (ArcType)a.ArcType, vertices);
        }
    }

    private static string? GetNodeId(Model? model) => model switch
    {
        PlaceNode pn      => pn.Data.Id,
        TransitionNode tn => tn.Data.Id,
        PortModel pm      => GetNodeId(pm.Parent),
        _ => null
    };
}
