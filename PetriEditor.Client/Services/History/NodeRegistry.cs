using Blazor.Diagrams.Core.Models;

namespace PetriNetAnalyzer.Services.History;

/// <summary>
/// Maps a stable domain ID (e.g. <c>Place.Id</c> / <c>Transition.Id</c>) to the
/// currently live <see cref="NodeModel"/> instance in the diagram.
///
/// Why this exists
/// ───────────────
/// Undo/redo commands may not capture a direct NodeModel reference, because each
/// "restore" operation creates a brand-new instance. The domain ID is the only
/// stable identity across those cycles. Commands look up the live instance
/// via <see cref="Find"/> immediately before they need it.
///
/// Lifecycle
/// ─────────
/// <see cref="Register"/> is called by <c>PetriNetManager</c> from the
/// <c>Diagram.Nodes.Added</c> event, which fires synchronously inside
/// <c>diagram.Nodes.Add()</c> — so the registry is always up to date by the
/// time any command code runs after the Add call.
/// <see cref="Unregister"/> is called from <c>Diagram.Nodes.Removed</c>.
/// </summary>
public sealed class NodeRegistry
{
    private readonly Dictionary<string, NodeModel> _map = new();
    private readonly IDiagramLogger _log;
    private const string Cat = "NodeRegistry";

    public NodeRegistry(IDiagramLogger? logger = null)
    {
        _log = logger ?? NullLogger.Instance;
    }

    public void Register(string id, NodeModel node)
    {
        _map[id] = node;
        _log.Log(Cat, $"Register id={Abbrev(id)} type={node.GetType().Name} total={_map.Count}");
    }

    public void Unregister(string id)
    {
        bool removed = _map.Remove(id);
        _log.Log(Cat, $"Unregister id={Abbrev(id)} found={removed} total={_map.Count}");
    }

    /// <returns>The live <see cref="NodeModel"/>, or <c>null</c> if the node is not in the diagram.</returns>
    public NodeModel? Find(string id)
    {
        bool found = _map.TryGetValue(id, out var node);
        _log.Log(Cat, $"Find id={Abbrev(id)} found={found}");
        return node;
    }

    private static string Abbrev(string id) =>
        id.Length <= 8 ? id : id[..8] + "…";
}
