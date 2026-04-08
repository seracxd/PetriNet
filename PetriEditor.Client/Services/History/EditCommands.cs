using Blazor.Diagrams.Core.Geometry;
using Blazor.Diagrams.Core.Models;
using PetriNetAnalyzer.DiagramModels;

namespace PetriNetAnalyzer.Services.History;

// ── Move node ─────────────────────────────────────────────────────────────────

/// <summary>Records a node drag. Holds a direct <see cref="NodeModel"/> reference
/// because the node is never recreated between the move and its undo.</summary>
public sealed class MoveNodeCommand : IDiagramCommand
{
    private readonly NodeModel _node;
    private readonly Point _from;
    private readonly Point _to;

    public MoveNodeCommand(NodeModel node, Point from, Point to)
    {
        _node = node;
        _from = from;
        _to   = to;
    }

    public bool IsStructural => false;  // position changes don't affect analysis
    public void Execute()   => _node.SetPosition(_to.X, _to.Y);
    public void Unexecute() => _node.SetPosition(_from.X, _from.Y);
}

// ── Edit place ────────────────────────────────────────────────────────────────

public sealed class EditPlaceCommand : IDiagramCommand
{
    private readonly PlaceNode _node;
    private readonly string _oldName;
    private readonly int _oldTokens;
    private readonly string _newName;
    private readonly int _newTokens;

    public EditPlaceCommand(
        PlaceNode node,
        string oldName, int oldTokens,
        string newName, int newTokens)
    {
        _node      = node;
        _oldName   = oldName;
        _oldTokens = oldTokens;
        _newName   = newName;
        _newTokens = newTokens;
    }

    public void Execute()   => Apply(_newName, _newTokens);
    public void Unexecute() => Apply(_oldName, _oldTokens);

    private void Apply(string name, int tokens)
    {
        _node.Data.Name   = name;
        _node.Title       = name;
        _node.Data.Tokens = tokens;
        _node.Refresh();
    }
}

// ── Edit transition ───────────────────────────────────────────────────────────

public sealed class EditTransitionCommand : IDiagramCommand
{
    private readonly TransitionNode _node;
    private readonly string _oldName;
    private readonly int _oldPriority;
    private readonly string _newName;
    private readonly int _newPriority;

    public EditTransitionCommand(
        TransitionNode node,
        string oldName, int oldPriority,
        string newName, int newPriority)
    {
        _node        = node;
        _oldName     = oldName;
        _oldPriority = oldPriority;
        _newName     = newName;
        _newPriority = newPriority;
    }

    public void Execute()   => Apply(_newName, _newPriority);
    public void Unexecute() => Apply(_oldName, _oldPriority);

    private void Apply(string name, int priority)
    {
        _node.Data.Name     = name;
        _node.Title         = name;
        _node.Data.Priority = priority;
        _node.Refresh();
    }
}

// ── Edit link ─────────────────────────────────────────────────────────────────

public sealed class EditLinkCommand : IDiagramCommand
{
    private readonly PetriLinkModel _link;
    private readonly int _oldWeight;
    private readonly Core.Models.ArcType _oldType;
    private readonly int _newWeight;
    private readonly Core.Models.ArcType _newType;

    public EditLinkCommand(
        PetriLinkModel link,
        int oldWeight, Core.Models.ArcType oldType,
        int newWeight, Core.Models.ArcType newType)
    {
        _link      = link;
        _oldWeight = oldWeight;
        _oldType   = oldType;
        _newWeight = newWeight;
        _newType   = newType;
    }

    public void Execute()   => Apply(_newWeight, _newType);
    public void Unexecute() => Apply(_oldWeight, _oldType);

    private void Apply(int weight, Core.Models.ArcType type)
    {
        _link.Weight  = weight;
        _link.ArcType = type;
        _link.Refresh();
    }
}

// ── Composite command ─────────────────────────────────────────────────────────

/// <summary>
/// Groups multiple commands into a single undo/redo step.
///
/// Execute   → runs each sub-command in order (first → last).
/// Unexecute → runs each sub-command in reverse (last → first),
///             which is the correct order for reversing a sequence of operations.
/// </summary>
public sealed class CompositeCommand : IDiagramCommand
{
    private readonly IReadOnlyList<IDiagramCommand> _commands;
    private readonly IDiagramLogger _log;

    public CompositeCommand(IEnumerable<IDiagramCommand> commands, IDiagramLogger? log = null)
    {
        _commands = commands.ToArray();
        _log      = log ?? NullLogger.Instance;
    }

    public void Execute()
    {
        _log.Log("Composite", $"Execute count={_commands.Count} [{Names()}]");
        foreach (var cmd in _commands)
            cmd.Execute();
    }

    public void Unexecute()
    {
        _log.Log("Composite", $"Unexecute count={_commands.Count} (reversed)");
        foreach (var cmd in _commands.Reverse())
            cmd.Unexecute();
    }

    private string Names() => string.Join(", ", _commands.Select(c => c.GetType().Name));
}
