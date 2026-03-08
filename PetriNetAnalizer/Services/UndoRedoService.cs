using Blazor.Diagrams.Core.Anchors;
using Blazor.Diagrams.Core.Geometry;
using Blazor.Diagrams.Core.Models;
using Core.Models;
using PetriNetAnalyzer.DiagramModels;

namespace PetriNetAnalyzer.Services;

// ── Command interface ────────────────────────────────────────────────────────

public interface IDiagramCommand
{
    void Execute();
    void Unexecute();
}

// ── Undo/Redo stack ──────────────────────────────────────────────────────────

public class UndoRedoService
{
    private readonly Stack<IDiagramCommand> _undoStack = new();
    private readonly Stack<IDiagramCommand> _redoStack = new();

    public bool CanUndo => _undoStack.Count > 0;
    public bool CanRedo => _redoStack.Count > 0;

    public event Action? HistoryChanged;

    /// <summary>Record and execute a new command (clears the redo stack).</summary>
    public void Execute(IDiagramCommand cmd)
    {
        cmd.Execute();
        _undoStack.Push(cmd);
        _redoStack.Clear();
        HistoryChanged?.Invoke();
    }

    /// <summary>Record a command that has already been executed externally.</summary>
    public void Record(IDiagramCommand cmd)
    {
        _undoStack.Push(cmd);
        _redoStack.Clear();
        HistoryChanged?.Invoke();
    }

    public void Undo()
    {
        if (!CanUndo) return;
        var cmd = _undoStack.Pop();
        cmd.Unexecute();
        _redoStack.Push(cmd);
        HistoryChanged?.Invoke();
    }

    public void Redo()
    {
        if (!CanRedo) return;
        var cmd = _redoStack.Pop();
        cmd.Execute();
        _undoStack.Push(cmd);
        HistoryChanged?.Invoke();
    }

    public void Clear()
    {
        _undoStack.Clear();
        _redoStack.Clear();
        HistoryChanged?.Invoke();
    }
}

// ── Concrete commands ────────────────────────────────────────────────────────

/// <summary>Add a node to the diagram.</summary>
public class AddNodeCommand : IDiagramCommand
{
    private readonly Blazor.Diagrams.BlazorDiagram _diagram;
    private readonly NodeModel _node;

    public AddNodeCommand(Blazor.Diagrams.BlazorDiagram diagram, NodeModel node)
    {
        _diagram = diagram;
        _node = node;
    }

    public void Execute() => _diagram.Nodes.Add(_node);
    public void Unexecute() => _diagram.Nodes.Remove(_node);
}

/// <summary>Remove a node (and all its connected links) from the diagram.</summary>
public class RemoveNodeCommand : IDiagramCommand
{
    private readonly Blazor.Diagrams.BlazorDiagram _diagram;
    private readonly NodeModel _node;
    // Snapshot of links connected to this node so they can be restored
    private List<LinkSnapshot>? _connectedLinks;

    public RemoveNodeCommand(Blazor.Diagrams.BlazorDiagram diagram, NodeModel node)
    {
        _diagram = diagram;
        _node = node;
    }

    public void Execute()
    {
        // Capture connected links before removing the node
        _connectedLinks = _diagram.Links
            .OfType<PetriLinkModel>()
            .Where(l => GetParentNode(l.Source) == _node || GetParentNode(l.Target) == _node)
            .Select(l => new LinkSnapshot(l))
            .ToList();

        _diagram.Nodes.Remove(_node);
    }

    public void Unexecute()
    {
        _diagram.Nodes.Add(_node);

        // Restore connected links
        if (_connectedLinks != null)
        {
            foreach (var snap in _connectedLinks)
                snap.Restore(_diagram);
        }
    }

    private static NodeModel? GetParentNode(Anchor a) => a.Model switch
    {
        NodeModel n => n,
        PortModel pm => pm.Parent,
        _ => null
    };
}

/// <summary>Add a link to the diagram.</summary>
public class AddLinkCommand : IDiagramCommand
{
    private readonly Blazor.Diagrams.BlazorDiagram _diagram;
    private readonly PetriLinkModel _link;

    public AddLinkCommand(Blazor.Diagrams.BlazorDiagram diagram, PetriLinkModel link)
    {
        _diagram = diagram;
        _link = link;
    }

    public void Execute() => _diagram.Links.Add(_link);
    public void Unexecute() => _diagram.Links.Remove(_link);
}

/// <summary>Remove a link from the diagram.</summary>
public class RemoveLinkCommand : IDiagramCommand
{
    private readonly Blazor.Diagrams.BlazorDiagram _diagram;
    private readonly PetriLinkModel _link;

    public RemoveLinkCommand(Blazor.Diagrams.BlazorDiagram diagram, PetriLinkModel link)
    {
        _diagram = diagram;
        _link = link;
    }

    public void Execute() => _diagram.Links.Remove(_link);
    public void Unexecute() => _diagram.Links.Add(_link);
}

/// <summary>Move a node from one position to another.</summary>
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

    public void Execute() => _node.SetPosition(_to.X, _to.Y);
    public void Unexecute() => _node.SetPosition(_from.X, _from.Y);
}

/// <summary>Change a place's name and/or token count.</summary>
public class EditPlaceCommand : IDiagramCommand
{
    private readonly PlaceNode _node;
    private readonly string _oldName, _newName;
    private readonly int _oldTokens, _newTokens;

    public EditPlaceCommand(PlaceNode node, string oldName, int oldTokens, string newName, int newTokens)
    {
        _node = node;
        _oldName = oldName; _newName = newName;
        _oldTokens = oldTokens; _newTokens = newTokens;
    }

    public void Execute() { _node.Data.Name = _newName; _node.Title = _newName; _node.Data.Tokens = _newTokens; _node.Refresh(); }
    public void Unexecute() { _node.Data.Name = _oldName; _node.Title = _oldName; _node.Data.Tokens = _oldTokens; _node.Refresh(); }
}

/// <summary>Change a transition's name and/or priority.</summary>
public class EditTransitionCommand : IDiagramCommand
{
    private readonly TransitionNode _node;
    private readonly string _oldName, _newName;
    private readonly int _oldPriority, _newPriority;

    public EditTransitionCommand(TransitionNode node, string oldName, int oldPriority, string newName, int newPriority)
    {
        _node = node;
        _oldName = oldName; _newName = newName;
        _oldPriority = oldPriority; _newPriority = newPriority;
    }

    public void Execute() { _node.Data.Name = _newName; _node.Title = _newName; _node.Data.Priority = _newPriority; _node.Refresh(); }
    public void Unexecute() { _node.Data.Name = _oldName; _node.Title = _oldName; _node.Data.Priority = _oldPriority; _node.Refresh(); }
}

/// <summary>Change a link's weight and/or arc type.</summary>
public class EditLinkCommand : IDiagramCommand
{
    private readonly PetriLinkModel _link;
    private readonly int _oldWeight, _newWeight;
    private readonly ArcType _oldType, _newType;

    public EditLinkCommand(PetriLinkModel link, int oldWeight, ArcType oldType, int newWeight, ArcType newType)
    {
        _link = link;
        _oldWeight = oldWeight; _newWeight = newWeight;
        _oldType = oldType; _newType = newType;
    }

    public void Execute() { _link.Weight = _newWeight; _link.ArcType = _newType; _link.Refresh(); }
    public void Unexecute() { _link.Weight = _oldWeight; _link.ArcType = _oldType; _link.Refresh(); }
}

// ── Composite command (wraps multiple commands as one undo step) ─────────────

public class CompositeCommand : IDiagramCommand
{
    private readonly List<IDiagramCommand> _commands;

    public CompositeCommand(IEnumerable<IDiagramCommand> commands)
        => _commands = commands.ToList();

    public void Execute()
    {
        foreach (var c in _commands) c.Execute();
    }

    public void Unexecute()
    {
        // Reverse order so undo is symmetric
        foreach (var c in Enumerable.Reverse(_commands)) c.Unexecute();
    }
}


/// <summary>Captures the full state of a PetriLinkModel so it can be re-added.</summary>
public class LinkSnapshot
{
    private readonly PetriLinkModel _link;

    public LinkSnapshot(PetriLinkModel link) => _link = link;

    public void Restore(Blazor.Diagrams.BlazorDiagram diagram)
    {
        if (!diagram.Links.Contains(_link))
            diagram.Links.Add(_link);
    }
}