using Blazor.Diagrams.Core.Anchors;
using Blazor.Diagrams.Core.Geometry;
using Blazor.Diagrams.Core.Models;
using Core.Models;
using PetriNetAnalyzer.DiagramModels;

namespace PetriNetAnalyzer.Services;

public interface IDiagramCommand
{
    void Execute();
    void Unexecute();

    /// <summary>
    /// True for commands that affect net behaviour (topology, weights, tokens, priorities,
    /// arc types). False for cosmetic changes like node moves. Structural commands
    /// invalidate cached analysis results.
    /// </summary>
    bool IsStructural => true;
}

public class UndoRedoService
{
    private readonly Stack<IDiagramCommand> _undoStack = new();
    private readonly Stack<IDiagramCommand> _redoStack = new();
    private readonly IDiagramLogger _log;
    private const string CAT = "History";

    public bool CanUndo => _undoStack.Count > 0;
    public bool CanRedo => _redoStack.Count > 0;
    public bool IsBusy { get; private set; }

    private int _batchDepth;
    private bool _isUndoRedoBusy;

    public event Action? HistoryChanged;
    /// <summary>Fired only when a structurally significant command is executed, undone, or redone.</summary>
    public event Action? StructuralChanged;

    public UndoRedoService(IDiagramLogger? logger = null)
    {
        _log = logger ?? NullLogger.Instance;
    }

    public void BeginBatch()
    {
        _batchDepth++;
        IsBusy = true;
        _log.Log(DiagramLogLevel.Debug, CAT, $"BeginBatch depth={_batchDepth}");
    }

    public void EndBatch()
    {
        if (_batchDepth > 0)
            _batchDepth--;

        if (_batchDepth == 0 && !_isUndoRedoBusy)
            IsBusy = false;

        _log.Log(DiagramLogLevel.Debug, CAT, $"EndBatch depth={_batchDepth} IsBusy={IsBusy}");
    }

    public void Execute(IDiagramCommand cmd)
    {
        _log.Log(DiagramLogLevel.Info, CAT, $"Execute {cmd.GetType().Name} undoStack={_undoStack.Count}");
        cmd.Execute();
        _undoStack.Push(cmd);
        _redoStack.Clear();
        HistoryChanged?.Invoke();
        if (cmd.IsStructural) StructuralChanged?.Invoke();
    }

    public void Record(IDiagramCommand cmd)
    {
        if (IsBusy)
        {
            _log.Log(DiagramLogLevel.Debug, CAT, $"Record dropped (IsBusy) cmd={cmd.GetType().Name}");
            return;
        }

        _log.Log(DiagramLogLevel.Info, CAT, $"Record {cmd.GetType().Name} undoStack->{_undoStack.Count + 1}");
        _undoStack.Push(cmd);
        _redoStack.Clear();
        HistoryChanged?.Invoke();
        if (cmd.IsStructural) StructuralChanged?.Invoke();
    }

    public void Undo()
    {
        if (!CanUndo)
        {
            _log.Log(DiagramLogLevel.Debug, CAT, "Undo skipped - stack empty");
            return;
        }

        _isUndoRedoBusy = true;
        IsBusy = true;
        try
        {
            var cmd = _undoStack.Pop();
            _log.Log(DiagramLogLevel.Info, CAT, $"Undo {cmd.GetType().Name} undoStack->{_undoStack.Count}");
            cmd.Unexecute();
            _redoStack.Push(cmd);
            if (cmd.IsStructural) StructuralChanged?.Invoke();
        }
        catch (Exception ex)
        {
            _log.Log(DiagramLogLevel.Error, CAT, $"Undo exception: {ex.GetType().Name}: {ex.Message}");
            throw;
        }
        finally
        {
            _isUndoRedoBusy = false;
            if (_batchDepth == 0)
                IsBusy = false;
        }

        HistoryChanged?.Invoke();
    }

    public void Redo()
    {
        if (!CanRedo)
        {
            _log.Log(DiagramLogLevel.Debug, CAT, "Redo skipped - stack empty");
            return;
        }

        _isUndoRedoBusy = true;
        IsBusy = true;
        try
        {
            var cmd = _redoStack.Pop();
            _log.Log(DiagramLogLevel.Info, CAT, $"Redo {cmd.GetType().Name} redoStack->{_redoStack.Count}");
            cmd.Execute();
            _undoStack.Push(cmd);
            if (cmd.IsStructural) StructuralChanged?.Invoke();
        }
        catch (Exception ex)
        {
            _log.Log(DiagramLogLevel.Error, CAT, $"Redo exception: {ex.GetType().Name}: {ex.Message}");
            throw;
        }
        finally
        {
            _isUndoRedoBusy = false;
            if (_batchDepth == 0)
                IsBusy = false;
        }

        HistoryChanged?.Invoke();
    }

    public void Clear()
    {
        _log.Log(DiagramLogLevel.Info, CAT, "Clear");
        _undoStack.Clear();
        _redoStack.Clear();
        HistoryChanged?.Invoke();
    }

    public IReadOnlyList<string> UndoStackNames => _undoStack.Select(c => c.GetType().Name).ToArray();
}

public class NodeRegistry
{
    private readonly Dictionary<string, NodeModel> _map = new();
    private readonly IDiagramLogger _log;
    private const string CAT = "NodeRegistry";

    public NodeRegistry(IDiagramLogger? logger = null)
    {
        _log = logger ?? NullLogger.Instance;
    }

    public void Register(string id, NodeModel node)
    {
        _map[id] = node;
        _log.Log(DiagramLogLevel.Debug, CAT, $"Register id={Short(id)} type={node.GetType().Name} total={_map.Count}");
    }

    public void Unregister(string id)
    {
        bool had = _map.Remove(id);
        _log.Log(DiagramLogLevel.Debug, CAT, $"Unregister id={Short(id)} found={had} total={_map.Count}");
    }

    public NodeModel? Find(string id)
    {
        bool found = _map.TryGetValue(id, out var n);
        _log.Log(DiagramLogLevel.Trace, CAT, $"Find id={Short(id)} found={found}");
        return n;
    }

    private static string Short(string id) => id[..Math.Min(8, id.Length)] + "…";
}

public record PlaceSnapshot(string Id, string Name, int Tokens, double X, double Y, double Size);
public record TransitionSnapshot(string Id, string Name, int Priority, double X, double Y, double Width, double Height);

public record LinkSnapshot(
    string SourceNodeId,
    string TargetNodeId,
    int Weight,
    ArcType ArcType,
    List<Point> VertexPositions,
    int WeightLabelSegment,
    bool WeightLabelFlipped
);

internal static class LinkHelpers
{
    public static string? GetDataId(NodeModel? node) => node switch
    {
        PlaceNode pn => pn.Data.Id,
        TransitionNode tn => tn.Data.Id,
        _ => null
    };

    public static LinkSnapshot Snapshot(PetriLinkModel link)
    {
        var src = GetParentNode(link.Source);
        var tgt = GetParentNode(link.Target);
        return new LinkSnapshot(
            GetDataId(src) ?? string.Empty,
            GetDataId(tgt) ?? string.Empty,
            link.Weight,
            link.ArcType,
            link.Vertices.Select(v => v.Position).ToList(),
            link.WeightLabelSegment,
            link.WeightLabelFlipped
        );
    }

    public static void Restore(
        Blazor.Diagrams.BlazorDiagram diagram,
        NodeRegistry registry,
        LinkSnapshot snap,
        IDiagramLogger? log,
        DiagramSettings? settings = null)
    {
        const string CAT = "LinkRestore";

        if (string.IsNullOrEmpty(snap.SourceNodeId) || string.IsNullOrEmpty(snap.TargetNodeId))
        {
            log?.Log(DiagramLogLevel.Warning, CAT, "Restore skipped - empty endpoint id");
            return;
        }

        var srcNode = registry.Find(snap.SourceNodeId);
        var tgtNode = registry.Find(snap.TargetNodeId);
        if (srcNode == null || tgtNode == null)
        {
            log?.Log(DiagramLogLevel.Debug, CAT,
                $"Restore skipped - missing node src={Short(snap.SourceNodeId)}={srcNode != null} tgt={Short(snap.TargetNodeId)}={tgtNode != null}");
            return;
        }

        bool exists = FindLive(diagram, snap.SourceNodeId, snap.TargetNodeId) != null;
        if (exists)
        {
            log?.Log(DiagramLogLevel.Debug, CAT, $"Restore skipped - link already exists src={Short(snap.SourceNodeId)} tgt={Short(snap.TargetNodeId)}");
            return;
        }

        var link = new PetriLinkModel(
            new EdgeIntersectionAnchor(srcNode),
            new EdgeIntersectionAnchor(tgtNode))
        {
            Segmentable = false,
            TargetMarker = LinkMarker.Arrow,
            Color = settings?.ArcColor ?? "black",
            SelectedColor = settings?.ArcSelectedColor ?? "#007bff",
            Weight = snap.Weight,
            ArcType = snap.ArcType,
            WeightLabelSegment = snap.WeightLabelSegment,
            WeightLabelFlipped = snap.WeightLabelFlipped,
            CanonicalSourceId = snap.SourceNodeId,
        };

        foreach (var vp in snap.VertexPositions)
            link.Vertices.Add(new PetriVertexModel(link, vp));

        diagram.Links.Add(link);
        log?.Log(DiagramLogLevel.Debug, CAT, $"Restore ok src={Short(snap.SourceNodeId)} tgt={Short(snap.TargetNodeId)} vertices={snap.VertexPositions.Count}");
    }

    public static PetriLinkModel? FindLive(Blazor.Diagrams.BlazorDiagram diagram, string sourceId, string targetId) =>
        diagram.Links.OfType<PetriLinkModel>().FirstOrDefault(l =>
            GetDataId(GetParentNode(l.Source)) == sourceId &&
            GetDataId(GetParentNode(l.Target)) == targetId);

    public static NodeModel? GetParentNode(Anchor a) => a.Model switch
    {
        NodeModel n => n,
        PortModel pm => pm.Parent,
        _ => null
    };

    private static string Short(string id) => id[..Math.Min(8, id.Length)] + "…";
}

public class AddNodeCommand : IDiagramCommand
{
    private readonly Blazor.Diagrams.BlazorDiagram _diagram;
    private readonly IDiagramLogger _log;
    private readonly PlaceSnapshot? _place;
    private readonly TransitionSnapshot? _transition;
    private readonly DiagramSettings? _settings;

    public AddNodeCommand(Blazor.Diagrams.BlazorDiagram diagram, NodeModel node, IDiagramLogger? log = null, DiagramSettings? settings = null)
    {
        _diagram = diagram;
        _log = log ?? NullLogger.Instance;
        _settings = settings;

        switch (node)
        {
            case PlaceNode place:
                _place = new PlaceSnapshot(
                    place.Data.Id,
                    place.Data.Name,
                    place.Data.Tokens,
                    place.Position?.X ?? 0,
                    place.Position?.Y ?? 0,
                    place.Size?.Width ?? settings?.PlaceSize ?? 60.0);
                break;
            case TransitionNode transition:
                _transition = new TransitionSnapshot(
                    transition.Data.Id,
                    transition.Data.Name,
                    transition.Data.Priority,
                    transition.Position?.X ?? 0,
                    transition.Position?.Y ?? 0,
                    transition.Size?.Width ?? settings?.TransitionWidth ?? 20.0,
                    transition.Size?.Height ?? settings?.TransitionHeight ?? 60.0);
                break;
            default:
                throw new InvalidOperationException($"Unsupported node type {node.GetType().Name}");
        }
    }

    public void Execute()
    {
        if (_place != null)
        {
            if (FindNode(_place.Id) != null)
            {
                _log.Log(DiagramLogLevel.Debug, "AddNode", $"Execute skipped - place already present id={Short(_place.Id)}");
                return;
            }

            var place = new Place
            {
                Id = _place.Id,
                Name = _place.Name,
                Tokens = _place.Tokens,
                Width = _place.Size,
                Height = _place.Size
            };
            var node = new PlaceNode(place, _settings);
            node.Size = new Blazor.Diagrams.Core.Geometry.Size(_place.Size, _place.Size);
            node.SetPosition(_place.X, _place.Y);
            _diagram.Nodes.Add(node);
            return;
        }

        if (_transition != null)
        {
            if (FindNode(_transition.Id) != null)
            {
                _log.Log(DiagramLogLevel.Debug, "AddNode", $"Execute skipped - transition already present id={Short(_transition.Id)}");
                return;
            }

            var transition = new Transition
            {
                Id = _transition.Id,
                Name = _transition.Name,
                Priority = _transition.Priority,
                Width = _transition.Width,
                Height = _transition.Height
            };
            var node = new TransitionNode(transition, _settings);
            node.Size = new Blazor.Diagrams.Core.Geometry.Size(_transition.Width, _transition.Height);
            node.SetPosition(_transition.X, _transition.Y);
            _diagram.Nodes.Add(node);
        }
    }

    public void Unexecute()
    {
        var id = _place?.Id ?? _transition?.Id;
        if (id == null)
            return;

        var live = FindNode(id);
        _log.Log(DiagramLogLevel.Debug, "AddNode", $"Unexecute id={Short(id)} found={live != null}");
        if (live != null)
            _diagram.Nodes.Remove(live);
    }

    private NodeModel? FindNode(string id) => _diagram.Nodes.FirstOrDefault(n => LinkHelpers.GetDataId(n) == id);

    private static string Short(string id) => id[..Math.Min(8, id.Length)] + "…";
}

public class RemovePlaceCommand : IDiagramCommand
{
    private readonly Blazor.Diagrams.BlazorDiagram _diagram;
    private readonly NodeRegistry _registry;
    private readonly PlaceSnapshot _snap;
    private readonly List<LinkSnapshot> _linkSnaps;
    private readonly IDiagramLogger _log;
    private readonly DiagramSettings? _settings;

    public RemovePlaceCommand(
        Blazor.Diagrams.BlazorDiagram diagram,
        NodeRegistry registry,
        PlaceNode node,
        IEnumerable<LinkSnapshot> connectedLinkSnaps,
        IDiagramLogger? log = null,
        DiagramSettings? settings = null)
    {
        _diagram = diagram;
        _registry = registry;
        _log = log ?? NullLogger.Instance;
        _settings = settings;
        _snap = new PlaceSnapshot(
            node.Data.Id,
            node.Data.Name,
            node.Data.Tokens,
            node.Position?.X ?? 0,
            node.Position?.Y ?? 0,
            node.Size?.Width ?? settings?.PlaceSize ?? 60.0);
        _linkSnaps = connectedLinkSnaps.ToList();
    }

    public void Execute()
    {
        var node = _registry.Find(_snap.Id);
        _log.Log(DiagramLogLevel.Info, "RemovePlace", $"Execute id={Short(_snap.Id)} hit={node != null}");
        if (node != null)
            _diagram.Nodes.Remove(node);
    }

    public void Unexecute()
    {
        if (_registry.Find(_snap.Id) != null)
            return;

        var data = new Place
        {
            Id = _snap.Id,
            Name = _snap.Name,
            Tokens = _snap.Tokens,
            Width = _snap.Size,
            Height = _snap.Size
        };
        var node = new PlaceNode(data, _settings);
        node.Size = new Blazor.Diagrams.Core.Geometry.Size(_snap.Size, _snap.Size);
        node.SetPosition(_snap.X, _snap.Y);
        _diagram.Nodes.Add(node);

        foreach (var ls in _linkSnaps)
            LinkHelpers.Restore(_diagram, _registry, ls, _log, _settings);
    }

    private static string Short(string id) => id[..Math.Min(8, id.Length)] + "…";
}

public class RemoveTransitionCommand : IDiagramCommand
{
    private readonly Blazor.Diagrams.BlazorDiagram _diagram;
    private readonly NodeRegistry _registry;
    private readonly TransitionSnapshot _snap;
    private readonly List<LinkSnapshot> _linkSnaps;
    private readonly IDiagramLogger _log;
    private readonly DiagramSettings? _settings;

    public RemoveTransitionCommand(
        Blazor.Diagrams.BlazorDiagram diagram,
        NodeRegistry registry,
        TransitionNode node,
        IEnumerable<LinkSnapshot> connectedLinkSnaps,
        IDiagramLogger? log = null,
        DiagramSettings? settings = null)
    {
        _diagram = diagram;
        _registry = registry;
        _log = log ?? NullLogger.Instance;
        _settings = settings;
        _snap = new TransitionSnapshot(
            node.Data.Id,
            node.Data.Name,
            node.Data.Priority,
            node.Position?.X ?? 0,
            node.Position?.Y ?? 0,
            node.Size?.Width ?? settings?.TransitionWidth ?? 20.0,
            node.Size?.Height ?? settings?.TransitionHeight ?? 60.0);
        _linkSnaps = connectedLinkSnaps.ToList();
    }

    public void Execute()
    {
        var node = _registry.Find(_snap.Id);
        _log.Log(DiagramLogLevel.Info, "RemoveTrans", $"Execute id={Short(_snap.Id)} hit={node != null}");
        if (node != null)
            _diagram.Nodes.Remove(node);
    }

    public void Unexecute()
    {
        if (_registry.Find(_snap.Id) != null)
            return;

        var data = new Transition
        {
            Id = _snap.Id,
            Name = _snap.Name,
            Priority = _snap.Priority,
            Width = _snap.Width,
            Height = _snap.Height,
        };
        var node = new TransitionNode(data, _settings);
        node.Size = new Blazor.Diagrams.Core.Geometry.Size(_snap.Width, _snap.Height);
        node.SetPosition(_snap.X, _snap.Y);
        _diagram.Nodes.Add(node);

        foreach (var ls in _linkSnaps)
            LinkHelpers.Restore(_diagram, _registry, ls, _log, _settings);
    }

    private static string Short(string id) => id[..Math.Min(8, id.Length)] + "…";
}

public class RemoveLinkCommand : IDiagramCommand
{
    private readonly Blazor.Diagrams.BlazorDiagram _diagram;
    private readonly NodeRegistry _registry;
    private readonly LinkSnapshot _snap;
    private readonly IDiagramLogger _log;
    private readonly DiagramSettings? _settings;

    public RemoveLinkCommand(
        Blazor.Diagrams.BlazorDiagram diagram,
        NodeRegistry registry,
        PetriLinkModel link,
        IDiagramLogger? log = null,
        DiagramSettings? settings = null)
    {
        _diagram = diagram;
        _registry = registry;
        _log = log ?? NullLogger.Instance;
        _settings = settings;
        _snap = LinkHelpers.Snapshot(link);
    }

    public void Execute()
    {
        var link = LinkHelpers.FindLive(_diagram, _snap.SourceNodeId, _snap.TargetNodeId);
        if (link != null)
            _diagram.Links.Remove(link);
    }

    public void Unexecute() => LinkHelpers.Restore(_diagram, _registry, _snap, _log, _settings);
}

public class AddLinkCommand : IDiagramCommand
{
    private readonly Blazor.Diagrams.BlazorDiagram _diagram;
    private readonly NodeRegistry _registry;
    private readonly LinkSnapshot _snap;
    private readonly IDiagramLogger _log;
    private readonly DiagramSettings? _settings;

    public AddLinkCommand(
        Blazor.Diagrams.BlazorDiagram diagram,
        NodeRegistry registry,
        PetriLinkModel link,
        IDiagramLogger? log = null,
        DiagramSettings? settings = null)
    {
        _diagram = diagram;
        _registry = registry;
        _log = log ?? NullLogger.Instance;
        _settings = settings;
        _snap = LinkHelpers.Snapshot(link);
    }

    public void Execute()
    {
        if (LinkHelpers.FindLive(_diagram, _snap.SourceNodeId, _snap.TargetNodeId) == null)
            LinkHelpers.Restore(_diagram, _registry, _snap, _log, _settings);
    }

    public void Unexecute()
    {
        var link = LinkHelpers.FindLive(_diagram, _snap.SourceNodeId, _snap.TargetNodeId);
        if (link != null)
            _diagram.Links.Remove(link);
    }
}

public class ReconnectLinkCommand : IDiagramCommand
{
    private readonly Blazor.Diagrams.BlazorDiagram _diagram;
    private readonly NodeRegistry _registry;
    private readonly LinkSnapshot _before;
    private readonly LinkSnapshot _after;
    private readonly IDiagramLogger _log;
    private readonly DiagramSettings? _settings;

    public ReconnectLinkCommand(
        Blazor.Diagrams.BlazorDiagram diagram,
        NodeRegistry registry,
        LinkSnapshot before,
        LinkSnapshot after,
        IDiagramLogger? log = null,
        DiagramSettings? settings = null)
    {
        _diagram = diagram;
        _registry = registry;
        _before = before;
        _after = after;
        _log = log ?? NullLogger.Instance;
        _settings = settings;
    }

    public void Execute()
    {
        var oldLive = LinkHelpers.FindLive(_diagram, _before.SourceNodeId, _before.TargetNodeId);
        if (oldLive != null)
            _diagram.Links.Remove(oldLive);
        LinkHelpers.Restore(_diagram, _registry, _after, _log, _settings);
    }

    public void Unexecute()
    {
        var newLive = LinkHelpers.FindLive(_diagram, _after.SourceNodeId, _after.TargetNodeId);
        if (newLive != null)
            _diagram.Links.Remove(newLive);
        LinkHelpers.Restore(_diagram, _registry, _before, _log, _settings);
    }
}

public class MoveNodeCommand : IDiagramCommand
{
    private readonly NodeModel _node;
    private readonly Point _from;
    private readonly Point _to;

    public MoveNodeCommand(NodeModel node, Point from, Point to)
    {
        _node = node;
        _from = from;
        _to = to;
    }

    public bool IsStructural => false;
    public void Execute() => _node.SetPosition(_to.X, _to.Y);
    public void Unexecute() => _node.SetPosition(_from.X, _from.Y);
}

public class MoveVertexCommand : IDiagramCommand
{
    private readonly PetriVertexModel _vertex;
    private readonly PetriLinkModel _link;
    private readonly Point _from;
    private readonly Point _to;

    public MoveVertexCommand(PetriVertexModel vertex, PetriLinkModel link, Point from, Point to)
    {
        _vertex = vertex;
        _link = link;
        _from = from;
        _to = to;
    }

    public bool IsStructural => false;
    public void Execute() { _vertex.Position = _to; _vertex.Refresh(); _link.Refresh(); }
    public void Unexecute() { _vertex.Position = _from; _vertex.Refresh(); _link.Refresh(); }
}

public class RemoveVertexCommand : IDiagramCommand
{
    private readonly PetriLinkModel _link;
    private readonly PetriVertexModel _vertex;
    private readonly int _index;

    public RemoveVertexCommand(PetriLinkModel link, PetriVertexModel vertex)
    {
        _link = link;
        _vertex = vertex;
        _index = Math.Max(0, link.Vertices.IndexOf(vertex));
    }

    public void Execute() { _link.Vertices.Remove(_vertex); _link.Refresh(); }
    public void Unexecute() { _link.Vertices.Insert(Math.Min(_index, _link.Vertices.Count), _vertex); _link.Refresh(); }
}

public class MoveWeightLabelCommand : IDiagramCommand
{
    private readonly PetriLinkModel _link;
    private readonly int _fromSegment;
    private readonly bool _fromFlipped;
    private readonly int _toSegment;
    private readonly bool _toFlipped;

    public MoveWeightLabelCommand(PetriLinkModel link, int fromSegment, bool fromFlipped, int toSegment, bool toFlipped)
    {
        _link = link;
        _fromSegment = fromSegment;
        _fromFlipped = fromFlipped;
        _toSegment = toSegment;
        _toFlipped = toFlipped;
    }

    public void Execute()
    {
        _link.WeightLabelSegment = _toSegment;
        _link.WeightLabelFlipped = _toFlipped;
        _link.Refresh();
    }

    public void Unexecute()
    {
        _link.WeightLabelSegment = _fromSegment;
        _link.WeightLabelFlipped = _fromFlipped;
        _link.Refresh();
    }
}

public class EditPlaceCommand : IDiagramCommand
{
    private readonly PlaceNode _node;
    private readonly string _oldName;
    private readonly string _newName;
    private readonly int _oldTokens;
    private readonly int _newTokens;

    public EditPlaceCommand(PlaceNode node, string oldName, int oldTokens, string newName, int newTokens)
    {
        _node = node;
        _oldName = oldName;
        _newName = newName;
        _oldTokens = oldTokens;
        _newTokens = newTokens;
    }

    public void Execute()
    {
        _node.Data.Name = _newName;
        _node.Title = _newName;
        _node.Data.Tokens = _newTokens;
        _node.Refresh();
    }

    public void Unexecute()
    {
        _node.Data.Name = _oldName;
        _node.Title = _oldName;
        _node.Data.Tokens = _oldTokens;
        _node.Refresh();
    }
}

public class EditTransitionCommand : IDiagramCommand
{
    private readonly TransitionNode _node;
    private readonly string _oldName;
    private readonly string _newName;
    private readonly int _oldPriority;
    private readonly int _newPriority;

    public EditTransitionCommand(TransitionNode node, string oldName, int oldPriority, string newName, int newPriority)
    {
        _node = node;
        _oldName = oldName;
        _newName = newName;
        _oldPriority = oldPriority;
        _newPriority = newPriority;
    }

    public void Execute()
    {
        _node.Data.Name = _newName;
        _node.Title = _newName;
        _node.Data.Priority = _newPriority;
        _node.Refresh();
    }

    public void Unexecute()
    {
        _node.Data.Name = _oldName;
        _node.Title = _oldName;
        _node.Data.Priority = _oldPriority;
        _node.Refresh();
    }
}

public class EditLinkCommand : IDiagramCommand
{
    private readonly PetriLinkModel _link;
    private readonly int _oldWeight;
    private readonly int _newWeight;
    private readonly ArcType _oldType;
    private readonly ArcType _newType;

    public EditLinkCommand(PetriLinkModel link, int oldWeight, ArcType oldType, int newWeight, ArcType newType)
    {
        _link = link;
        _oldWeight = oldWeight;
        _newWeight = newWeight;
        _oldType = oldType;
        _newType = newType;
    }

    public void Execute() { _link.Weight = _newWeight; _link.ArcType = _newType; _link.Refresh(); }
    public void Unexecute() { _link.Weight = _oldWeight; _link.ArcType = _oldType; _link.Refresh(); }
}

public class CompositeCommand : IDiagramCommand
{
    private readonly List<IDiagramCommand> _commands;
    private readonly IDiagramLogger _log;

    public CompositeCommand(IEnumerable<IDiagramCommand> commands, IDiagramLogger? log = null)
    {
        _commands = commands.ToList();
        _log = log ?? NullLogger.Instance;
    }

    public void Execute()
    {
        _log.Log(DiagramLogLevel.Info, "Composite", $"Execute count={_commands.Count}");
        foreach (var c in _commands)
            c.Execute();
    }

    public void Unexecute()
    {
        _log.Log(DiagramLogLevel.Info, "Composite", $"Unexecute count={_commands.Count} (reverse)");
        foreach (var c in Enumerable.Reverse(_commands))
            c.Unexecute();
    }
}