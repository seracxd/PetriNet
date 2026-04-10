using Blazor.Diagrams.Core.Anchors;
using Blazor.Diagrams.Core.Models;
using PetriNetAnalyzer.DiagramModels;

namespace PetriNetAnalyzer.Services.History;

/// <summary>
/// Static helpers shared by multiple commands:
/// resolving anchors to their parent NodeModel, extracting domain IDs,
/// snapshotting links, and restoring links from snapshots.
/// </summary>
internal static class DiagramHelpers
{
    // ── Anchor → NodeModel ─────────────────────────────────────────────────────

    /// <summary>
    /// Returns the <see cref="NodeModel"/> that owns the given anchor,
    /// unwrapping port ownership when necessary.
    /// </summary>
    public static NodeModel? GetParentNode(Anchor anchor) => anchor.Model switch
    {
        NodeModel n  => n,
        PortModel pm => pm.Parent,
        _            => null
    };

    // ── Domain ID ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the stable domain ID (e.g. <c>Place.Id</c>) for <paramref name="node"/>,
    /// or <c>null</c> for unrecognised node types.
    ///
    /// Use this instead of <c>NodeModel.Id</c> when you need an ID that survives
    /// undo/redo cycles that recreate nodes as fresh instances.
    /// </summary>
    public static string? GetDomainId(NodeModel? node) => node switch
    {
        PlaceNode      pn => pn.Data.Id,
        TransitionNode tn => tn.Data.Id,
        _                 => null
    };

    // ── Snapshot ──────────────────────────────────────────────────────────────

    /// <summary>Captures the current state of <paramref name="link"/> as an immutable snapshot.</summary>
    public static LinkSnapshot SnapshotLink(PetriLinkModel link)
    {
        var srcNode = GetParentNode(link.Source);
        var tgtNode = GetParentNode(link.Target);

        return new LinkSnapshot(
            SourceNodeId:      GetDomainId(srcNode) ?? string.Empty,
            TargetNodeId:      GetDomainId(tgtNode) ?? string.Empty,
            Weight:            link.Weight,
            ArcType:           link.ArcType,
            VertexPositions:   link.Vertices.Select(v => v.Position).ToArray(),
            WeightLabelSegment: link.WeightLabelSegment,
            WeightLabelFlipped: link.WeightLabelFlipped);
    }

    // ── Restore ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Reconstructs a <see cref="PetriLinkModel"/> from <paramref name="snapshot"/> and
    /// adds it to <paramref name="diagram"/>.
    ///
    /// Preconditions (all checked internally — method is a no-op when they fail):
    /// <list type="bullet">
    ///   <item>Both endpoint domain IDs must be non-empty.</item>
    ///   <item>Both nodes must be present in <paramref name="registry"/>.</item>
    ///   <item>No link with the same (source, target) pair may already exist (idempotency guard).</item>
    /// </list>
    /// </summary>
    public static void RestoreLink(
        Blazor.Diagrams.BlazorDiagram diagram,
        NodeRegistry registry,
        LinkSnapshot snapshot,
        IDiagramLogger? log = null)
    {
        const string Cat = "RestoreLink";

        if (string.IsNullOrEmpty(snapshot.SourceNodeId) || string.IsNullOrEmpty(snapshot.TargetNodeId))
        {
            log?.Log(Cat, "Skipped — snapshot has empty node ID");
            return;
        }

        var srcNode = registry.Find(snapshot.SourceNodeId);
        var tgtNode = registry.Find(snapshot.TargetNodeId);

        if (srcNode == null || tgtNode == null)
        {
            log?.Log(Cat, $"Skipped — node(s) missing from registry " +
                          $"src={Abbrev(snapshot.SourceNodeId)} present={srcNode != null}  " +
                          $"tgt={Abbrev(snapshot.TargetNodeId)} present={tgtNode != null}");
            return;
        }

        // Idempotency: don't add a duplicate link
        bool alreadyExists = diagram.Links
            .OfType<PetriLinkModel>()
            .Any(l => GetDomainId(GetParentNode(l.Source)) == snapshot.SourceNodeId &&
                      GetDomainId(GetParentNode(l.Target)) == snapshot.TargetNodeId);

        if (alreadyExists)
        {
            log?.Log(Cat, $"Skipped — link already present src={Abbrev(snapshot.SourceNodeId)}");
            return;
        }

        var link = new PetriLinkModel(
            new EdgeIntersectionAnchor(srcNode),
            new EdgeIntersectionAnchor(tgtNode))
        {
            Segmentable          = false,
            TargetMarker         = LinkMarker.Arrow,
            Color                = "black",
            SelectedColor        = "#007bff",
            Weight               = snapshot.Weight,
            ArcType              = snapshot.ArcType,
            WeightLabelSegment   = snapshot.WeightLabelSegment,
            WeightLabelFlipped   = snapshot.WeightLabelFlipped,
            CanonicalSourceId    = snapshot.SourceNodeId,
        };

        foreach (var pos in snapshot.VertexPositions)
            link.Vertices.Add(new PetriVertexModel(link, pos));

        diagram.Links.Add(link);
        log?.Log(Cat, $"Restored src={Abbrev(snapshot.SourceNodeId)} weight={snapshot.Weight} " +
                      $"vertices={snapshot.VertexPositions.Count}");
    }

    private static string Abbrev(string id) =>
        id.Length <= 8 ? id : id[..8] + "…";
}
