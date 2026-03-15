using Blazor.Diagrams.Core.Geometry;
using PetriNetAnalyzer.DiagramModels;

namespace PetriNetAnalyzer.Services.History;

// ── Add link ──────────────────────────────────────────────────────────────────

/// <summary>
/// Records a link that was already added to the diagram by the user.
///
/// Execute   → if the link is gone (was previously unexecuted), restore it from snapshot.
/// Unexecute → remove the live link from the diagram.
/// </summary>
public sealed class AddLinkCommand : IDiagramCommand
{
    private readonly Blazor.Diagrams.BlazorDiagram _diagram;
    private readonly NodeRegistry _registry;
    private readonly LinkSnapshot _snap;
    private readonly IDiagramLogger _log;

    public AddLinkCommand(
        Blazor.Diagrams.BlazorDiagram diagram,
        NodeRegistry registry,
        PetriLinkModel link,
        IDiagramLogger? log = null)
    {
        _diagram  = diagram;
        _registry = registry;
        _log      = log ?? NullLogger.Instance;
        _snap     = DiagramHelpers.SnapshotLink(link);
    }

    public void Execute()
    {
        _log.Log("AddLink", "Execute");
        // Restore from snapshot if the live link is no longer in the diagram.
        if (FindLive() == null)
            DiagramHelpers.RestoreLink(_diagram, _registry, _snap, _log);
    }

    public void Unexecute()
    {
        var live = FindLive();
        _log.Log("AddLink", $"Unexecute found={live != null}");
        if (live != null) _diagram.Links.Remove(live);
    }

    private PetriLinkModel? FindLive() =>
        _diagram.Links
            .OfType<PetriLinkModel>()
            .FirstOrDefault(l =>
                DiagramHelpers.GetDomainId(DiagramHelpers.GetParentNode(l.Source)) == _snap.SourceNodeId &&
                DiagramHelpers.GetDomainId(DiagramHelpers.GetParentNode(l.Target)) == _snap.TargetNodeId);
}

// ── Remove link ───────────────────────────────────────────────────────────────

/// <summary>
/// Records the deletion of a standalone arc (both endpoints survive).
///
/// Execute   → removes the live link.
/// Unexecute → restores the link from its snapshot.
/// </summary>
public sealed class RemoveLinkCommand : IDiagramCommand
{
    private readonly Blazor.Diagrams.BlazorDiagram _diagram;
    private readonly NodeRegistry _registry;
    private readonly LinkSnapshot _snap;
    private readonly IDiagramLogger _log;

    public RemoveLinkCommand(
        Blazor.Diagrams.BlazorDiagram diagram,
        NodeRegistry registry,
        PetriLinkModel link,
        IDiagramLogger? log = null)
    {
        _diagram  = diagram;
        _registry = registry;
        _log      = log ?? NullLogger.Instance;
        _snap     = DiagramHelpers.SnapshotLink(link);
        _log.Log("RemoveLink",
            $"Snapshot src={Abbrev(_snap.SourceNodeId)} tgt={Abbrev(_snap.TargetNodeId)}");
    }

    public void Execute()
    {
        var live = FindLive();
        _log.Log("RemoveLink", $"Execute found={live != null}");
        if (live != null) _diagram.Links.Remove(live);
    }

    public void Unexecute() =>
        DiagramHelpers.RestoreLink(_diagram, _registry, _snap, _log);

    private PetriLinkModel? FindLive() =>
        _diagram.Links
            .OfType<PetriLinkModel>()
            .FirstOrDefault(l =>
                DiagramHelpers.GetDomainId(DiagramHelpers.GetParentNode(l.Source)) == _snap.SourceNodeId &&
                DiagramHelpers.GetDomainId(DiagramHelpers.GetParentNode(l.Target)) == _snap.TargetNodeId);

    private static string Abbrev(string id) => id.Length <= 8 ? id : id[..8] + "…";
}

// ── Move vertex ───────────────────────────────────────────────────────────────

public sealed class MoveVertexCommand : IDiagramCommand
{
    private readonly PetriVertexModel _vertex;
    private readonly PetriLinkModel _link;
    private readonly Point _from;
    private readonly Point _to;

    public MoveVertexCommand(PetriVertexModel vertex, PetriLinkModel link, Point from, Point to)
    {
        _vertex = vertex;
        _link   = link;
        _from   = from;
        _to     = to;
    }

    public void Execute()   => Apply(_to);
    public void Unexecute() => Apply(_from);

    private void Apply(Point p) { _vertex.Position = p; _vertex.Refresh(); _link.Refresh(); }
}

// ── Remove vertex ─────────────────────────────────────────────────────────────

public sealed class RemoveVertexCommand : IDiagramCommand
{
    private readonly PetriLinkModel _link;
    private readonly PetriVertexModel _vertex;
    private readonly int _index;

    public RemoveVertexCommand(PetriLinkModel link, PetriVertexModel vertex)
    {
        _link   = link;
        _vertex = vertex;
        // Capture position before removal
        _index  = Math.Max(0, link.Vertices.IndexOf(vertex));
    }

    public void Execute()
    {
        _link.Vertices.Remove(_vertex);
        _link.Refresh();
    }

    public void Unexecute()
    {
        _link.Vertices.Insert(Math.Min(_index, _link.Vertices.Count), _vertex);
        _link.Refresh();
    }
}

// ── Move weight label ─────────────────────────────────────────────────────────

public sealed class MoveWeightLabelCommand : IDiagramCommand
{
    private readonly PetriLinkModel _link;
    private readonly int _fromSegment;
    private readonly bool _fromFlipped;
    private readonly int _toSegment;
    private readonly bool _toFlipped;

    public MoveWeightLabelCommand(
        PetriLinkModel link,
        int fromSegment, bool fromFlipped,
        int toSegment,   bool toFlipped)
    {
        _link         = link;
        _fromSegment  = fromSegment;
        _fromFlipped  = fromFlipped;
        _toSegment    = toSegment;
        _toFlipped    = toFlipped;
    }

    public void Execute()   => Apply(_toSegment, _toFlipped);
    public void Unexecute() => Apply(_fromSegment, _fromFlipped);

    private void Apply(int segment, bool flipped)
    {
        _link.WeightLabelSegment = segment;
        _link.WeightLabelFlipped = flipped;
        _link.Refresh();
    }
}
