using Blazor.Diagrams.Core.Models;
using Core.Models;
using PetriNetAnalyzer.DiagramModels;

namespace PetriNetAnalyzer.Services.History;

// ── Add place ─────────────────────────────────────────────────────────────────

/// <summary>
/// Records the addition of a new Place node.
///
/// Why snapshot-based instead of holding a direct NodeModel reference
/// ──────────────────────────────────────────────────────────────────
/// Holding a reference and calling _diagram.Nodes.Add(_node) on redo looks
/// simple but breaks badly:
///
///   1. Blazor.Diagrams internally tracks removed nodes. Re-adding the *same
///      object* can leave it in an inconsistent state (ports, size, events).
///   2. After Undo(Add) the node is removed. After Redo(Add) a fresh node is
///      created by a paired RemovePlace.Unexecute. Now the registry points to
///      the new instance, but AddNode._node still points to the old one.
///      The next Undo(Add) calls Remove on the stale reference — the diagram
///      ignores it and the node becomes undeletable.
///   3. Repeated Undo/Redo cycles therefore accumulate ghost nodes.
///
/// The fix: capture a snapshot in the constructor and always build a *fresh*
/// NodeModel on every Execute (redo). This mirrors exactly what
/// RemovePlaceCommand.Unexecute does, making the two perfectly symmetrical.
/// </summary>
public sealed class AddPlaceCommand : IDiagramCommand
{
    private readonly Blazor.Diagrams.BlazorDiagram _diagram;
    private readonly NodeRegistry _registry;
    private readonly PlaceSnapshot _snap;
    private readonly IDiagramLogger _log;

    public AddPlaceCommand(
        Blazor.Diagrams.BlazorDiagram diagram,
        NodeRegistry registry,
        PlaceNode node,
        IDiagramLogger? log = null)
    {
        _diagram = diagram;
        _registry = registry;
        _log = log ?? NullLogger.Instance;

        _snap = new PlaceSnapshot(
            Id: node.Data.Id,
            Name: node.Data.Name,
            Tokens: node.Data.Tokens,
            X: node.Position?.X ?? 0,
            Y: node.Position?.Y ?? 0);
    }

    public void Execute()
    {
        // Guard: don't double-add on repeated redo
        if (_registry.Find(_snap.Id) != null)
        {
            _log.Log("AddPlace", $"Execute skipped — already present id={Abbrev(_snap.Id)}");
            return;
        }

        _log.Log("AddPlace", $"Execute id={Abbrev(_snap.Id)} name={_snap.Name}");
        var place = new Place { Id = _snap.Id, Name = _snap.Name, Tokens = _snap.Tokens };
        var node = new PlaceNode(place);
        node.SetPosition(_snap.X, _snap.Y);
        _diagram.Nodes.Add(node); // fires NodeRegistry.Register synchronously
    }

    public void Unexecute()
    {
        var node = _registry.Find(_snap.Id);
        _log.Log("AddPlace", $"Unexecute id={Abbrev(_snap.Id)} found={node != null}");
        if (node != null) _diagram.Nodes.Remove(node); // fires NodeRegistry.Unregister synchronously
    }

    private static string Abbrev(string id) => id.Length <= 8 ? id : id[..8] + "…";
}

// ── Add transition ────────────────────────────────────────────────────────────

/// <summary>
/// Records the addition of a new Transition node.
/// See <see cref="AddPlaceCommand"/> for the full design rationale.
/// </summary>
public sealed class AddTransitionCommand : IDiagramCommand
{
    private readonly Blazor.Diagrams.BlazorDiagram _diagram;
    private readonly NodeRegistry _registry;
    private readonly TransitionSnapshot _snap;
    private readonly IDiagramLogger _log;

    public AddTransitionCommand(
        Blazor.Diagrams.BlazorDiagram diagram,
        NodeRegistry registry,
        TransitionNode node,
        IDiagramLogger? log = null)
    {
        _diagram = diagram;
        _registry = registry;
        _log = log ?? NullLogger.Instance;

        _snap = new TransitionSnapshot(
            Id: node.Data.Id,
            Name: node.Data.Name,
            Priority: node.Data.Priority,
            X: node.Position?.X ?? 0,
            Y: node.Position?.Y ?? 0);
    }

    public void Execute()
    {
        if (_registry.Find(_snap.Id) != null)
        {
            _log.Log("AddTransition", $"Execute skipped — already present id={Abbrev(_snap.Id)}");
            return;
        }

        _log.Log("AddTransition", $"Execute id={Abbrev(_snap.Id)} name={_snap.Name}");
        var transition = new Transition { Id = _snap.Id, Name = _snap.Name, Priority = _snap.Priority };
        var node = new TransitionNode(transition);
        node.SetPosition(_snap.X, _snap.Y);
        _diagram.Nodes.Add(node);
    }

    public void Unexecute()
    {
        var node = _registry.Find(_snap.Id);
        _log.Log("AddTransition", $"Unexecute id={Abbrev(_snap.Id)} found={node != null}");
        if (node != null) _diagram.Nodes.Remove(node);
    }

    private static string Abbrev(string id) => id.Length <= 8 ? id : id[..8] + "…";
}

// ── Remove place ──────────────────────────────────────────────────────────────

/// <summary>
/// Records the deletion of a Place node and all arcs that touched it.
///
/// Unexecute restores ALL connected arcs (both incoming and outgoing).
/// The idempotency guard in <see cref="DiagramHelpers.RestoreLink"/> prevents
/// duplicates when multiple nodes sharing an arc are restored in one composite step.
/// </summary>
public sealed class RemovePlaceCommand : IDiagramCommand
{
    private readonly Blazor.Diagrams.BlazorDiagram _diagram;
    private readonly NodeRegistry _registry;
    private readonly PlaceSnapshot _nodeSnap;
    private readonly IReadOnlyList<LinkSnapshot> _linkSnaps;
    private readonly IDiagramLogger _log;

    public RemovePlaceCommand(
        Blazor.Diagrams.BlazorDiagram diagram,
        NodeRegistry registry,
        PlaceNode node,
        IEnumerable<LinkSnapshot> connectedLinkSnapshots,
        IDiagramLogger? log = null)
    {
        _diagram = diagram;
        _registry = registry;
        _log = log ?? NullLogger.Instance;

        _nodeSnap = new PlaceSnapshot(
            Id: node.Data.Id,
            Name: node.Data.Name,
            Tokens: node.Data.Tokens,
            X: node.Position?.X ?? 0,
            Y: node.Position?.Y ?? 0);

        _linkSnaps = connectedLinkSnapshots.ToArray();

        _log.Log("RemovePlace",
            $"Snapshot id={Abbrev(_nodeSnap.Id)} name={_nodeSnap.Name} links={_linkSnaps.Count}");
    }

    public void Execute()
    {
        var node = _registry.Find(_nodeSnap.Id);
        _log.Log("RemovePlace", $"Execute id={Abbrev(_nodeSnap.Id)} found={node != null}");
        if (node != null) _diagram.Nodes.Remove(node);
    }

    public void Unexecute()
    {
        // Guard: don't double-restore on repeated undo
        if (_registry.Find(_nodeSnap.Id) != null)
        {
            _log.Log("RemovePlace", $"Unexecute skipped — already present id={Abbrev(_nodeSnap.Id)}");
            return;
        }

        _log.Log("RemovePlace",
            $"Unexecute id={Abbrev(_nodeSnap.Id)} name={_nodeSnap.Name} links={_linkSnaps.Count}");

        var place = new Place { Id = _nodeSnap.Id, Name = _nodeSnap.Name, Tokens = _nodeSnap.Tokens };
        var node = new PlaceNode(place);
        node.SetPosition(_nodeSnap.X, _nodeSnap.Y);
        _diagram.Nodes.Add(node); // NodeRegistry.Register fires synchronously

        // Restore all arcs — idempotency guard in RestoreLink handles any duplicates
        foreach (var ls in _linkSnaps)
            DiagramHelpers.RestoreLink(_diagram, _registry, ls, _log);
    }

    private static string Abbrev(string id) => id.Length <= 8 ? id : id[..8] + "…";
}

// ── Remove transition ─────────────────────────────────────────────────────────

/// <summary>
/// Records the deletion of a Transition node and all arcs that touched it.
/// See <see cref="RemovePlaceCommand"/> for the full design rationale.
/// </summary>
public sealed class RemoveTransitionCommand : IDiagramCommand
{
    private readonly Blazor.Diagrams.BlazorDiagram _diagram;
    private readonly NodeRegistry _registry;
    private readonly TransitionSnapshot _nodeSnap;
    private readonly IReadOnlyList<LinkSnapshot> _linkSnaps;
    private readonly IDiagramLogger _log;

    public RemoveTransitionCommand(
        Blazor.Diagrams.BlazorDiagram diagram,
        NodeRegistry registry,
        TransitionNode node,
        IEnumerable<LinkSnapshot> connectedLinkSnapshots,
        IDiagramLogger? log = null)
    {
        _diagram = diagram;
        _registry = registry;
        _log = log ?? NullLogger.Instance;

        _nodeSnap = new TransitionSnapshot(
            Id: node.Data.Id,
            Name: node.Data.Name,
            Priority: node.Data.Priority,
            X: node.Position?.X ?? 0,
            Y: node.Position?.Y ?? 0);

        _linkSnaps = connectedLinkSnapshots.ToArray();

        _log.Log("RemoveTrans",
            $"Snapshot id={Abbrev(_nodeSnap.Id)} name={_nodeSnap.Name} links={_linkSnaps.Count}");
    }

    public void Execute()
    {
        var node = _registry.Find(_nodeSnap.Id);
        _log.Log("RemoveTrans", $"Execute id={Abbrev(_nodeSnap.Id)} found={node != null}");
        if (node != null) _diagram.Nodes.Remove(node);
    }

    public void Unexecute()
    {
        if (_registry.Find(_nodeSnap.Id) != null)
        {
            _log.Log("RemoveTrans", $"Unexecute skipped — already present id={Abbrev(_nodeSnap.Id)}");
            return;
        }

        _log.Log("RemoveTrans",
            $"Unexecute id={Abbrev(_nodeSnap.Id)} name={_nodeSnap.Name} links={_linkSnaps.Count}");

        var transition = new Transition { Id = _nodeSnap.Id, Name = _nodeSnap.Name, Priority = _nodeSnap.Priority };
        var node = new TransitionNode(transition);
        node.SetPosition(_nodeSnap.X, _nodeSnap.Y);
        _diagram.Nodes.Add(node);

        foreach (var ls in _linkSnaps)
            DiagramHelpers.RestoreLink(_diagram, _registry, ls, _log);
    }

    private static string Abbrev(string id) => id.Length <= 8 ? id : id[..8] + "…";
}