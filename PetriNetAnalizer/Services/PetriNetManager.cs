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
    public UndoRedoService History { get; } = new();

    private PetriLinkModel? _pendingLink;
    private MutablePositionAnchor? _pendingFloating;
    private PortModel? _pendingSourcePort;

    public event Action? PendingLinkChanged;
    public bool HasPendingLink => _pendingLink != null;

    private int _placeCounter = 1;
    private int _transitionCounter = 1;

    public PetriNetManager()
    {
        var options = new BlazorDiagramOptions
        {
            AllowMultiSelection = true,
            Virtualization = { Enabled = false },
            Zoom = { Minimum = 0.25, Maximum = 3.0 },
            Links =
            {
                DefaultRouter        = new NormalRouter(),
                DefaultPathGenerator = new StraightPathGenerator(radius: 20),
                Factory              = LinkFactory,
                SnappingRadius       = 3,
            }
        };

        Diagram = new BlazorDiagram(options);
        Diagram.UnregisterBehavior<DragNewLinkBehavior>();

        Diagram.RegisterComponent<PlaceNode, PlaceComponent>();
        Diagram.RegisterComponent<TransitionNode, TransitionComponent>();
        Diagram.RegisterComponent<PetriVertexModel, PetriVertexWidget>();
        Diagram.RegisterComponent<PetriWeightControl, PetriWeightControlWidget>();
        Diagram.RegisterComponent<PetriLinkModel, PetriLinkWidget>();

        Diagram.Links.Added += OnLinkAdded;
        Diagram.PointerMove += OnPointerMove;
        Diagram.PanChanged += OnPanChanged;
        Diagram.Nodes.Added += OnNodeAdded;
        Diagram.PointerDown += OnDiagramPointerDown;
        Diagram.PointerUp += OnDiagramPointerUp;
    }

    // ── Node drag tracking ────────────────────────────────────────────────────
    private Point? _dragStartPos;
    private NodeModel? _draggingNode;

    private void OnNodeAdded(NodeModel node) { /* hook for future per-node setup */ }

    private void OnDiagramPointerDown(Model? model, Blazor.Diagrams.Core.Events.PointerEventArgs e)
    {
        if (model is NodeModel node)
        {
            _draggingNode = node;
            _dragStartPos = node.Position;
        }
    }

    private void OnDiagramPointerUp(Model? model, Blazor.Diagrams.Core.Events.PointerEventArgs e)
    {
        if (_draggingNode != null && _dragStartPos != null && _draggingNode.Position != null)
        {
            var from = _dragStartPos;
            var to = _draggingNode.Position;
            if (Math.Abs(to.X - from.X) > 0.5 || Math.Abs(to.Y - from.Y) > 0.5)
                History.Record(new MoveNodeCommand(_draggingNode, from, to));
        }
        _draggingNode = null;
        _dragStartPos = null;
    }

    // ── Pan bounds ────────────────────────────────────────────────────────────
    // The virtual canvas is 10 000 x 10 000 diagram units — big but bounded.
    private const double PanBound = 5000.0;

    private void OnPanChanged()
    {
        var pan = Diagram.Pan;
        var zoom = Diagram.Zoom;

        // Maximum allowed pan offset in screen pixels at current zoom
        double maxPx = PanBound * zoom;

        double clampedX = Math.Clamp(pan.X, -maxPx, maxPx);
        double clampedY = Math.Clamp(pan.Y, -maxPx, maxPx);

        if (Math.Abs(clampedX - pan.X) > 0.5 || Math.Abs(clampedY - pan.Y) > 0.5)
            Diagram.SetPan(clampedX, clampedY);
    }

    // ── Tool control ──────────────────────────────────────────────────────────

    public void EnableLinkDrawing() { }
    public void DisableLinkDrawing() => CancelPendingLink();

    // ── Click-to-connect ──────────────────────────────────────────────────────

    public void HandleArcToolClick(Model? model, Point clientPoint)
    {
        var pos = Diagram.GetRelativeMousePoint(clientPoint.X, clientPoint.Y);

        if (_pendingLink == null)
        {
            StartPendingLink(model, pos);
        }
        else
        {
            if (model == null)
                AddVertexToPending(pos);
            else if (model is PortModel port)
                FinishPendingLink(port.Parent, port, pos);
            else if (model is NodeModel node)
                FinishPendingLink(node, FindClosestPort(node, pos), pos);
            else if (model is PetriLinkModel clickedLink)
            {
                // If clicking on the pending link itself, the widget's OnHitArcTool
                // already called AddVertexToPending — don't add a second vertex here
                if (clickedLink != _pendingLink)
                    AddVertexToPending(pos);
            }
            else if (model is PetriVertexModel v)
            {
                // Only add vertex if it's not already on the pending link
                if (v.Parent != _pendingLink)
                    AddVertexToPending(pos);
            }
        }
    }

    private void StartPendingLink(Model? model, Point pos)
    {
        PortModel? sourcePort;
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
        else return;

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

    public void AddVertexToPending(Point pos)
    {
        if (_pendingLink == null || _pendingFloating == null) return;
        _pendingLink.Vertices.Add(new PetriVertexModel(_pendingLink, pos));
        // Do NOT move _pendingFloating — it keeps following the mouse as the live tip
        _pendingLink.Refresh();
    }

    public void AddVertexToLink(PetriLinkModel link, Point diagramPos)
    {
        var pts = GetFullLinkPointsForLink(link);
        if (pts.Count < 2) return;

        int bestSeg = 0;
        double bestD = double.MaxValue;
        for (int i = 0; i < pts.Count - 1; i++)
        {
            double d = DistPtSegSq(diagramPos, pts[i], pts[i + 1]);
            if (d < bestD) { bestD = d; bestSeg = i; }
        }

        int insertAt = Math.Max(0, Math.Min(bestSeg, link.Vertices.Count));
        link.Vertices.Insert(insertAt, new PetriVertexModel(link, diagramPos));
        link.Refresh();
    }

    /// <summary>
    /// Starts an endpoint drag (reconnect) directly from the widget.
    /// isSource=true drags the source end, false drags the target end.
    /// </summary>
    public void StartEndpointDrag(PetriLinkModel link, bool isSource, double clientX, double clientY)
    {
        var snapshotSource = link.Source;
        var snapshotTarget = link.Target;
        var weight = link.Weight;
        var arcType = link.ArcType;
        var canonicalSourceId = link.CanonicalSourceId;
        var vertexPositions = link.Vertices.Select(v => v.Position).ToList();

        var fixedAnchor = isSource ? link.Target : link.Source;
        var fixedIsSource = !isSource;

        Diagram.Links.Remove(link);

        var mousePos = Diagram.GetRelativeMousePoint(clientX, clientY);
        var floatingAnchor = new MutablePositionAnchor(mousePos);

        PetriLinkModel tempLink = fixedIsSource
            ? new PetriLinkModel(fixedAnchor, floatingAnchor)
            : new PetriLinkModel(floatingAnchor, fixedAnchor);

        tempLink.Segmentable = false;
        tempLink.TargetMarker = LinkMarker.Arrow;
        tempLink.Color = "black";
        tempLink.SelectedColor = "#007bff";
        tempLink.Weight = weight;
        tempLink.ArcType = arcType;
        tempLink.CanonicalSourceId = canonicalSourceId;
        tempLink.IsDraggingEndpoint = true;
        tempLink.SnapshotSource = snapshotSource;
        tempLink.SnapshotTarget = snapshotTarget;

        foreach (var vp in vertexPositions)
            tempLink.Vertices.Add(new PetriVertexModel(tempLink, vp));

        Diagram.Links.Add(tempLink);

        void OnMove(Model? _, Blazor.Diagrams.Core.Events.PointerEventArgs me)
        {
            floatingAnchor.SetPosition(Diagram.GetRelativeMousePoint(me.ClientX, me.ClientY));
            tempLink.Refresh();
        }

        void OnUp(Model? _, Blazor.Diagrams.Core.Events.PointerEventArgs ue)
        {
            Diagram.PointerMove -= OnMove;
            Diagram.PointerUp -= OnUp;
            tempLink.IsDraggingEndpoint = false;

            var dropPos = Diagram.GetRelativeMousePoint(ue.ClientX, ue.ClientY);
            var fixedNode = GetParentNode(fixedAnchor);
            var hitNode = Diagram.Nodes.FirstOrDefault(n => n != fixedNode && HitTest(n, dropPos));

            if (hitNode == null)
            {
                Diagram.Links.Remove(tempLink);
                RestoreLink(snapshotSource, snapshotTarget, weight, arcType, canonicalSourceId, vertexPositions);
                return;
            }

            var closestPort = FindClosestPort(hitNode, dropPos);
            Anchor nodeAnchor = closestPort != null
                ? (Anchor)new SinglePortAnchor(closestPort) { MiddleIfNoMarker = false, UseShapeAndAlignment = true }
                : new ShapeIntersectionAnchor(hitNode);

            if (fixedIsSource) tempLink.SetTarget(nodeAnchor);
            else tempLink.SetSource(nodeAnchor);

            tempLink.SnapshotSource = null;
            tempLink.SnapshotTarget = null;
            tempLink.Refresh();

            var srcNode = GetParentNode(tempLink.Source);
            var tgtNode = GetParentNode(tempLink.Target);
            if (srcNode != null && tgtNode != null)
            {
                if (ValidatePetriLink(tempLink, srcNode, tgtNode))
                    AddControls(tempLink);
            }
        }

        Diagram.PointerMove += OnMove;
        Diagram.PointerUp += OnUp;
    }

    private void RestoreLink(Anchor src, Anchor tgt, int weight, ArcType arcType,
                             string? canonicalSourceId, List<Point> vertices)
    {
        var restored = new PetriLinkModel(src, tgt)
        {
            Segmentable = false,
            TargetMarker = LinkMarker.Arrow,
            Color = "black",
            SelectedColor = "#007bff",
            Weight = weight,
            ArcType = arcType,
            CanonicalSourceId = canonicalSourceId,
        };
        foreach (var vp in vertices)
            restored.Vertices.Add(new PetriVertexModel(restored, vp));
        Diagram.Links.Add(restored);
    }

    private void FinishPendingLink(NodeModel targetNode, PortModel? targetPort, Point pos)
    {
        if (_pendingLink == null) return;
        var sourceNode = GetParentNode(_pendingLink.Source);
        if (sourceNode == null) { CancelPendingLink(); return; }
        if (sourceNode.GetType() == targetNode.GetType()) { CancelPendingLink(); return; }

        var dup = Diagram.Links.OfType<LinkModel>()
            .Any(o => o != _pendingLink && GetParentNode(o.Source) == sourceNode && GetParentNode(o.Target) == targetNode);
        if (dup) { CancelPendingLink(); return; }

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
        History.Record(new AddLinkCommand(Diagram, finalised));
        Diagram.SelectModel(finalised, true);
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
        if (link.IsDraggingEndpoint) return;
        AddControls(link);
        link.TargetAttached += OnLinkTargetAttached;
    }

    private void AddControls(PetriLinkModel link)
    {
        // Only weight label — endpoint diamonds are rendered in PetriLinkWidget directly
        Diagram.Controls.AddFor(link).Add(new PetriWeightControl());
    }

    private async void OnLinkTargetAttached(BaseLinkModel baseLink)
    {
        if (baseLink is not PetriLinkModel link) return;
        if (link.IsDraggingEndpoint) return;
        await Task.Yield();
        var src = GetParentNode(link.Source);
        var tgt = GetParentNode(link.Target);
        if (src == null || tgt == null) return;
        if (ValidatePetriLink(link, src, tgt))
            if (link.CanonicalSourceId == null) link.CanonicalSourceId = src.Id;
    }

    private bool ValidatePetriLink(PetriLinkModel link, NodeModel src, NodeModel tgt)
    {
        if (src.GetType() == tgt.GetType()) { Diagram.Links.Remove(link); return false; }
        var dup = Diagram.Links.OfType<LinkModel>()
            .Any(o => o != link && GetParentNode(o.Source) == src && GetParentNode(o.Target) == tgt);
        if (dup) { Diagram.Links.Remove(link); return false; }
        return true;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private NodeModel? GetParentNode(Anchor anchor) => anchor.Model switch
    {
        NodeModel n => n,
        PortModel pm => pm.Parent,
        _ => null
    };

    public PortModel? FindClosestPort(NodeModel node, Point p)
    {
        if (!node.Ports.Any()) return null;
        return node.Ports.OrderBy(port =>
        {
            var pp = port.Position;
            if (pp == null) return double.MaxValue;
            double dx = pp.X - p.X, dy = pp.Y - p.Y;
            return dx * dx + dy * dy;
        }).First();
    }

    private LinkModel? LinkFactory(Diagram diagram, ILinkable source, Anchor? targetAnchor)
    {
        Anchor? src = source switch
        {
            NodeModel n => new ShapeIntersectionAnchor(n),
            PortModel pm => new SinglePortAnchor(pm) { MiddleIfNoMarker = false, UseShapeAndAlignment = true },
            _ => null
        };
        if (src is null) return null;
        return new PetriLinkModel(src, targetAnchor)
        {
            Segmentable = false,
            TargetMarker = LinkMarker.Arrow,
            Color = "black",
            SelectedColor = "#007bff",
        };
    }

    private List<Point> GetFullLinkPointsForLink(PetriLinkModel link)
    {
        var fallback = new Point(0, 0);
        var first = link.Vertices.Count > 0 ? link.Vertices[0].Position : fallback;
        var last = link.Vertices.Count > 0 ? link.Vertices[^1].Position : fallback;
        var srcPos = link.Source.GetPosition(link, new[] { first, first });
        var tgtPos = link.Target?.GetPosition(link, new[] { last, last });
        var pts = new List<Point>();
        if (srcPos != null) pts.Add(srcPos);
        pts.AddRange(link.Vertices.Select(v => v.Position));
        if (tgtPos != null) pts.Add(tgtPos);
        return pts;
    }

    private static bool HitTest(NodeModel node, Point p)
    {
        if (node.Position == null || node.Size == null) return false;
        const double snap = 12;
        return p.X >= node.Position.X - snap && p.X <= node.Position.X + node.Size.Width + snap &&
               p.Y >= node.Position.Y - snap && p.Y <= node.Position.Y + node.Size.Height + snap;
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

    // ── Node management ───────────────────────────────────────────────────────

    public void AddNodeAt(string type, Point clientPoint)
    {
        var point = Diagram.GetRelativeMousePoint(clientPoint.X, clientPoint.Y);
        NodeModel? node = type switch
        {
            "place" => new PlaceNode(new Place { Name = $"P{_placeCounter++}" }),
            "transition" => new TransitionNode(new Transition { Name = $"T{_transitionCounter++}" }),
            _ => null
        };
        if (node is null) return;
        node.Position = new Point(
            point.X - (node.Size?.Width ?? 60) / 2,
            point.Y - (node.Size?.Height ?? 60) / 2);
        // Record via command (Execute adds to diagram)
        History.Execute(new AddNodeCommand(Diagram, node));
        Diagram.SelectModel(node, true);
    }

    public void DeleteSelected()
    {
        var toDelete = Diagram.GetSelectedModels().ToList();
        Diagram.UnselectAll();

        var cmds = new List<IDiagramCommand>();
        foreach (var model in toDelete)
        {
            switch (model)
            {
                case PetriVertexModel v:
                    v.Parent.Vertices.Remove(v);
                    v.Parent.Refresh();
                    break;
                case NodeModel n:
                    cmds.Add(new RemoveNodeCommand(Diagram, n));
                    break;
                case PetriLinkModel l:
                    cmds.Add(new RemoveLinkCommand(Diagram, l));
                    break;
            }
        }

        if (cmds.Count == 0) return;

        // Execute all removes; wrap in a composite so they undo as one step
        var composite = new CompositeCommand(cmds);
        composite.Execute();
        History.Record(composite);
    }

    public void HandleDoubleClick(Model? model, Point clientPoint) { }

    public void ZoomIn() => Diagram.SetZoom(Math.Min(Diagram.Zoom * 1.2, 3.0));
    public void ZoomOut() => Diagram.SetZoom(Math.Max(Diagram.Zoom / 1.2, 0.25));
    public void ResetView()
    {
        Diagram.SetPan(0, 0);
        Diagram.SetZoom(1);
    }

    public void Dispose()
    {
        Diagram.Links.Added -= OnLinkAdded;
        Diagram.PointerMove -= OnPointerMove;
        Diagram.PanChanged -= OnPanChanged;
        Diagram.Nodes.Added -= OnNodeAdded;
        Diagram.PointerDown -= OnDiagramPointerDown;
        Diagram.PointerUp -= OnDiagramPointerUp;
    }
}