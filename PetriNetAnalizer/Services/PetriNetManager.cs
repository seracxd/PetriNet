using Blazor.Diagrams;
using Blazor.Diagrams.Core;
using Blazor.Diagrams.Core.Anchors;
using Blazor.Diagrams.Core.Behaviors;
using Blazor.Diagrams.Core.Geometry;
using Blazor.Diagrams.Core.Models;
using Blazor.Diagrams.Core.Models.Base;
using Blazor.Diagrams.Core.PathGenerators;
using Blazor.Diagrams.Core.Routers;
using Blazor.Diagrams.Options;
using Core.Models;
using PetriNetAnalyzer.Components.DiagramNodes;
using PetriNetAnalyzer.Components.Widgets;
using PetriNetAnalyzer.DiagramModels;

namespace PetriNetAnalyzer.Services;

public class PetriNetManager : IDisposable
{
    public BlazorDiagram Diagram { get; private set; }

    // ── Click-to-connect state ────────────────────────────────────────────────
    // When arc tool is active and the user clicks a port/node, we start a
    // pending link. Subsequent clicks on canvas add vertices; click on a
    // node/port finalises the link.

    private PetriLinkModel? _pendingLink;      // the in-progress arc
    private MutablePositionAnchor? _pendingFloating; // floating target follows mouse
    private PortModel? _pendingSourcePort;     // remembered source port (may be null)

    public event Action? PendingLinkChanged;   // lets Home.razor re-render cursor etc.
    public bool HasPendingLink => _pendingLink != null;

    private int _placeCounter = 1;
    private int _transitionCounter = 1;

    public PetriNetManager()
    {
        var options = new BlazorDiagramOptions
        {
            AllowMultiSelection = false,
            Virtualization = { Enabled = false },
            Links =
            {
                DefaultRouter        = new NormalRouter(),
                DefaultPathGenerator = new StraightPathGenerator(radius: 20),
                Factory              = LinkFactory,
                SnappingRadius       = 3,
            }
        };

        Diagram = new BlazorDiagram(options);

        // We manage link drawing entirely ourselves — no drag behaviour needed
        Diagram.UnregisterBehavior<DragNewLinkBehavior>();

        Diagram.RegisterComponent<PlaceNode, PlaceComponent>();
        Diagram.RegisterComponent<TransitionNode, TransitionComponent>();
        Diagram.RegisterComponent<PetriVertexModel, PetriVertexWidget>();
        Diagram.RegisterComponent<PetriWeightControl, PetriWeightControlWidget>();
        Diagram.RegisterComponent<PetriEndpointControl, PetriEndpointWidget>();

        Diagram.Links.Added += OnLinkAdded;
        Diagram.PointerMove += OnPointerMove;
    }

    // ── Tool control ──────────────────────────────────────────────────────────

    public void EnableLinkDrawing() { /* click-to-connect, no DragNewLinkBehavior needed */ }
    public void DisableLinkDrawing() => CancelPendingLink();

    // ── Click-to-connect entry point (called from Home.razor OnPointerDown) ───

    /// <summary>
    /// Main dispatcher for the arc tool.
    /// Called for every pointer-down when arc tool is active.
    /// model = whatever was clicked (port, node, link, null=canvas).
    /// </summary>
    public void HandleArcToolClick(Model? model, Point clientPoint)
    {
        var pos = Diagram.GetRelativeMousePoint(clientPoint.X, clientPoint.Y);

        if (_pendingLink == null)
        {
            // ── No pending link: start one from the clicked port/node ─────────
            StartPendingLink(model, pos);
        }
        else
        {
            // ── Pending link exists ───────────────────────────────────────────
            if (model == null)
            {
                // Clicked blank canvas → add vertex at this point
                AddVertexToPending(pos);
            }
            else if (model is PortModel port)
            {
                FinishPendingLink(port.Parent, port, pos);
            }
            else if (model is NodeModel node)
            {
                // Find closest port on the node to the click point
                var closestPort = FindClosestPort(node, pos);
                FinishPendingLink(node, closestPort, pos);
            }
            else if (model is PetriLinkModel || model is PetriVertexModel)
            {
                // Clicked an existing link/vertex — ignore, don't cancel
            }
            // Clicking something else (like the floating link itself) is ignored
        }
    }

    private void StartPendingLink(Model? model, Point pos)
    {
        PortModel? sourcePort = null;
        Anchor sourceAnchor;

        if (model is PortModel port)
        {
            sourcePort = port;
            sourceAnchor = new SinglePortAnchor(port) { MiddleIfNoMarker = false, UseShapeAndAlignment = true };
        }
        else if (model is NodeModel node)
        {
            var closest = FindClosestPort(node, pos);
            sourcePort = closest;
            sourceAnchor = closest != null
                ? (Anchor)new SinglePortAnchor(closest) { MiddleIfNoMarker = false, UseShapeAndAlignment = true }
                : new ShapeIntersectionAnchor(node);
        }
        else
        {
            return; // clicked nothing useful
        }

        _pendingSourcePort = sourcePort;
        _pendingFloating = new MutablePositionAnchor(pos);

        _pendingLink = new PetriLinkModel(sourceAnchor, _pendingFloating)
        {
            Segmentable = false,
            TargetMarker = LinkMarker.Arrow,
            Color = "#007bff",
            SelectedColor = "#007bff",
            IsDraggingEndpoint = true,
        };

        Diagram.Links.Add(_pendingLink);
        PendingLinkChanged?.Invoke();
    }

    private void AddVertexToPending(Point pos)
    {
        if (_pendingLink == null || _pendingFloating == null) return;

        // Insert a real vertex just before the floating target
        var vertex = new PetriVertexModel(_pendingLink, pos);
        _pendingLink.Vertices.Add(vertex);
        _pendingFloating.SetPosition(pos);
        _pendingLink.Refresh();
    }

    private void FinishPendingLink(NodeModel targetNode, PortModel? targetPort, Point pos)
    {
        if (_pendingLink == null) return;

        var sourceNode = GetParentNode(_pendingLink.Source);
        if (sourceNode == null) { CancelPendingLink(); return; }

        // Validate Petri rules before finalising
        if (sourceNode.GetType() == targetNode.GetType()) { CancelPendingLink(); return; }

        var duplicateExists = Diagram.Links
            .OfType<LinkModel>()
            .Any(other =>
                other != _pendingLink &&
                GetParentNode(other.Source) == sourceNode &&
                GetParentNode(other.Target) == targetNode);
        if (duplicateExists) { CancelPendingLink(); return; }

        // Attach real target anchor
        Anchor targetAnchor = targetPort != null
            ? (Anchor)new SinglePortAnchor(targetPort) { MiddleIfNoMarker = false, UseShapeAndAlignment = true }
            : new ShapeIntersectionAnchor(targetNode);

        _pendingLink.SetTarget(targetAnchor);
        _pendingLink.IsDraggingEndpoint = false;
        _pendingLink.Color = "black";
        _pendingLink.CanonicalSourceId = sourceNode.Id;

        var finalised = _pendingLink;
        _pendingLink = null;
        _pendingFloating = null;
        _pendingSourcePort = null;

        finalised.Refresh();
        AddControls(finalised);
        PendingLinkChanged?.Invoke();
    }

    public void CancelPendingLink()
    {
        if (_pendingLink == null) return;
        Diagram.Links.Remove(_pendingLink);
        _pendingLink = null;
        _pendingFloating = null;
        _pendingSourcePort = null;
        PendingLinkChanged?.Invoke();
    }

    private void OnPointerMove(Model? model, Blazor.Diagrams.Core.Events.PointerEventArgs e)
    {
        if (_pendingLink == null || _pendingFloating == null) return;
        _pendingFloating.SetPosition(Diagram.GetRelativeMousePoint(e.ClientX, e.ClientY));
        _pendingLink.Refresh();
    }

    // ── Link lifecycle ────────────────────────────────────────────────────────

    private void OnLinkAdded(BaseLinkModel baseLink)
    {
        if (baseLink is not PetriLinkModel link) return;
        if (link.IsDraggingEndpoint) return; // pending or temp link — skip

        AddControls(link);
        link.TargetAttached += OnLinkTargetAttached;
    }

    private void AddControls(PetriLinkModel link)
    {
        Diagram.Controls.AddFor(link)
            .Add(new PetriEndpointControl(isSourceEnd: true, validatePetriLink: RunValidation, addControls: AddControls, manager: this))
            .Add(new PetriEndpointControl(isSourceEnd: false, validatePetriLink: RunValidation, addControls: AddControls, manager: this))
            .Add(new PetriWeightControl());
    }

    private void RunValidation(PetriLinkModel link, NodeModel sourceNode, NodeModel targetNode)
        => ValidatePetriLink(link, sourceNode, targetNode);

    private async void OnLinkTargetAttached(BaseLinkModel baseLink)
    {
        if (baseLink is not PetriLinkModel link) return;
        if (link.IsDraggingEndpoint) return;

        await Task.Yield();

        var sourceNode = GetParentNode(link.Source);
        var targetNode = GetParentNode(link.Target);
        if (sourceNode == null || targetNode == null) return;

        if (!ValidatePetriLink(link, sourceNode, targetNode)) return;
        if (link.CanonicalSourceId == null) link.CanonicalSourceId = sourceNode.Id;
    }

    private bool ValidatePetriLink(PetriLinkModel link, NodeModel sourceNode, NodeModel targetNode)
    {
        if (sourceNode.GetType() == targetNode.GetType())
        {
            Diagram.Links.Remove(link);
            return false;
        }

        var duplicateExists = Diagram.Links
            .OfType<LinkModel>()
            .Any(other =>
                other != link &&
                GetParentNode(other.Source) == sourceNode &&
                GetParentNode(other.Target) == targetNode);

        if (duplicateExists)
        {
            Diagram.Links.Remove(link);
            return false;
        }

        return true;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private NodeModel? GetParentNode(Anchor anchor) => anchor.Model switch
    {
        NodeModel node => node,
        PortModel port => port.Parent,
        _ => null
    };

    /// <summary>Find the port on a node whose rendered position is closest to p.</summary>
    public PortModel? FindClosestPort(NodeModel node, Point p)
    {
        if (!node.Ports.Any()) return null;

        return node.Ports
            .OrderBy(port =>
            {
                var pp = port.Position;
                if (pp == null) return double.MaxValue;
                var dx = pp.X - p.X;
                var dy = pp.Y - p.Y;
                return dx * dx + dy * dy;
            })
            .First();
    }

    private LinkModel? LinkFactory(Diagram diagram, ILinkable source, Anchor? targetAnchor)
    {
        Anchor? sourceAnchor = source switch
        {
            NodeModel node => new ShapeIntersectionAnchor(node),
            PortModel port => new SinglePortAnchor(port) { MiddleIfNoMarker = false, UseShapeAndAlignment = true },
            _ => null
        };
        if (sourceAnchor is null) return null;

        return new PetriLinkModel(sourceAnchor, targetAnchor)
        {
            Segmentable = false,
            TargetMarker = LinkMarker.Arrow,
            Color = "black",
            SelectedColor = "#007bff",
        };
    }

    // ── Node management ───────────────────────────────────────────────────────

    public void AddNodeAt(string type, Point clientPoint)
    {
        var point = Diagram.GetRelativeMousePoint(clientPoint.X, clientPoint.Y);
        NodeModel? newNode = type switch
        {
            "place" => new PlaceNode(new Place { Name = $"P{_placeCounter++}" }),
            "transition" => new TransitionNode(new Transition { Name = $"T{_transitionCounter++}" }),
            _ => null
        };
        if (newNode is null) return;

        newNode.Position = new Point(
            point.X - (newNode.Size?.Width ?? 60) / 2,
            point.Y - (newNode.Size?.Height ?? 60) / 2);
        Diagram.Nodes.Add(newNode);
    }

    public void DeleteSelected()
    {
        foreach (var model in Diagram.GetSelectedModels().ToList())
        {
            switch (model)
            {
                case PetriVertexModel vertex:
                    vertex.Parent.Vertices.Remove(vertex);
                    vertex.Parent.Refresh();
                    break;
                case NodeModel node:
                    Diagram.Nodes.Remove(node);
                    break;
                case LinkModel link:
                    Diagram.Links.Remove(link);
                    break;
            }
        }
    }

    public void HandleDoubleClick(Model? model, Point clientPoint)
    {
        if (model is PetriVertexModel vertex)
            vertex.Parent.Vertices.Remove(vertex);
        // Double-click on link is now handled by single-click vertex insertion during pending
    }

    private List<Point> GetFullLinkPoints(LinkModel link, Point fallback)
    {
        var first = link.Vertices.Count > 0 ? link.Vertices[0].Position : fallback;
        var last = link.Vertices.Count > 0 ? link.Vertices[^1].Position : fallback;

        var sourcePos = link.Source.GetPosition(link, new[] { first, first });
        var targetPos = link.Target.GetPosition(link, new[] { last, last });

        var points = new List<Point>();
        if (sourcePos != null) points.Add(sourcePos);
        points.AddRange(link.Vertices.Select(v => v.Position));
        if (targetPos != null) points.Add(targetPos);
        return points;
    }

    private static int FindClosestSegmentIndex(IReadOnlyList<Point> pts, Point p)
    {
        int best = 0; double bestD = double.MaxValue;
        for (int i = 0; i < pts.Count - 1; i++)
        {
            var d = DistPtSegSq(p, pts[i], pts[i + 1]);
            if (d < bestD) { bestD = d; best = i; }
        }
        return best;
    }

    private static double DistPtSegSq(Point p, Point a, Point b)
    {
        double abx = b.X - a.X, aby = b.Y - a.Y;
        double apx = p.X - a.X, apy = p.Y - a.Y;
        double len2 = abx * abx + aby * aby;
        if (len2 < 1e-9) return apx * apx + apy * apy;
        double t = Math.Clamp((apx * abx + apy * aby) / len2, 0, 1);
        double dx = p.X - (a.X + t * abx), dy = p.Y - (a.Y + t * aby);
        return dx * dx + dy * dy;
    }

    public void Dispose()
    {
        Diagram.Links.Added -= OnLinkAdded;
        Diagram.PointerMove -= OnPointerMove;
    }
}