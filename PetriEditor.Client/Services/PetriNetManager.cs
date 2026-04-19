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
using PetriEditor.Client.Components.DiagramNodes;
using PetriEditor.Client.Components.Widgets;
using PetriNetAnalyzer.DiagramModels;
namespace PetriNetAnalyzer.Services;

public class PetriNetManager : IDisposable
{
    public BlazorDiagram Diagram { get; private set; }
    public IDiagramLogger Logger { get; }
    public UndoRedoService History { get; }
    public NodeRegistry NodeRegistry { get; }

    private readonly DiagramSettings _settings;

    private PetriLinkModel? _pendingLink;
    private MutablePositionAnchor? _pendingFloating;
    private PortModel? _pendingSourcePort;

    public event Action? PendingLinkChanged;
    public bool HasPendingLink => _pendingLink != null;

    /// <summary>Set by SimulationService — blocks node drag tracking while true.</summary>
    public bool IsSimulating { get; set; }

    private int _placeCounter = 1;
    private int _transitionCounter = 1;

    public PetriNetManager(IDiagramLogger logger, DiagramSettings settings)
    {
        Logger = logger;
        _settings = settings;
        History = new UndoRedoService(Logger);
        NodeRegistry = new NodeRegistry(Logger);

        Diagram = BuildDiagram();

        _settings.Changed += OnSettingsChanged;
    }

    private BlazorDiagram BuildDiagram()
    {
        var options = new BlazorDiagramOptions
        {
            AllowMultiSelection = true,
            Virtualization = { Enabled = false },
            Zoom = { Minimum = _settings.ZoomMin, Maximum = _settings.ZoomMax },
            Links =
            {
                DefaultRouter        = new NormalRouter(),
                DefaultPathGenerator = new StraightPathGenerator(radius: 20),
                Factory              = LinkFactory,
                SnappingRadius       = _settings.LinkSnappingRadius,
            }
        };

        var diagram = new BlazorDiagram(options);
        diagram.UnregisterBehavior<DragNewLinkBehavior>();
        diagram.UnregisterBehavior<PanBehavior>();
        diagram.UnregisterBehavior<KeyboardShortcutsBehavior>();

        diagram.RegisterComponent<PlaceNode, PlaceComponent>();
        diagram.RegisterComponent<TransitionNode, TransitionComponent>();
        diagram.RegisterComponent<PetriVertexModel, PetriVertexWidget>();
        diagram.RegisterComponent<PetriLinkModel, PetriLinkWidget>();

        diagram.Links.Added += OnLinkAdded;
        diagram.Links.Removed += OnLinkRemoved;
        diagram.PointerMove += OnPointerMove;
        diagram.PanChanged += OnPanChanged;
        diagram.Nodes.Added += OnNodeAdded;
        diagram.Nodes.Removed += OnNodeRemoved;
        diagram.PointerDown += OnDiagramPointerDown;
        diagram.PointerUp += OnDiagramPointerUp;

        return diagram;
    }

    // ── Settings changes ──────────────────────────────────────────────────────

    private void OnSettingsChanged()
    {
        Diagram.Options.Zoom.Minimum = _settings.ZoomMin;
        Diagram.Options.Zoom.Maximum = _settings.ZoomMax;
        Diagram.Options.Links.SnappingRadius = _settings.LinkSnappingRadius;

        foreach (var place in Diagram.Nodes.OfType<PlaceNode>())
        {
            place.Data.Width = _settings.PlaceSize;
            place.Data.Height = _settings.PlaceSize;
            place.Size = new Size(_settings.PlaceSize, _settings.PlaceSize);
            place.Refresh();
        }

        foreach (var transition in Diagram.Nodes.OfType<TransitionNode>())
        {
            transition.Data.Width = _settings.TransitionWidth;
            transition.Data.Height = _settings.TransitionHeight;
            transition.Size = new Size(_settings.TransitionWidth, _settings.TransitionHeight);
            transition.Refresh();
        }

        foreach (var link in Diagram.Links.OfType<PetriLinkModel>())
        {
            if (link.IsDraggingEndpoint)
                link.Color = _settings.ArcPendingColor;
            else
                link.Color = _settings.ArcColor;

            link.SelectedColor = _settings.ArcSelectedColor;
            link.Refresh();
        }

        Diagram.Refresh();
    }

    // ── Registry maintenance ──────────────────────────────────────────────────

    private void OnNodeAdded(NodeModel node)
    {
        var id = GetDomainId(node);
        if (id != null) NodeRegistry.Register(id, node);
    }

    private void OnNodeRemoved(NodeModel node)
    {
        var id = GetDomainId(node);
        if (id != null) NodeRegistry.Unregister(id);
    }

    // ── Node drag tracking ────────────────────────────────────────────────────

    private Point? _dragStartPos;
    private NodeModel? _draggingNode;

    private void OnDiagramPointerDown(Model? model, Blazor.Diagrams.Core.Events.PointerEventArgs e)
    {
        if (IsSimulating) return; // fully locked during simulation
        if (model is NodeModel node)
        {
            _draggingNode = node;
            _dragStartPos = node.Position;
        }
    }

    private void OnDiagramPointerUp(Model? model, Blazor.Diagrams.Core.Events.PointerEventArgs e)
    {
        if (IsSimulating) { _draggingNode = null; _dragStartPos = null; return; }
        if (_draggingNode != null && _dragStartPos != null && _draggingNode.Position != null)
        {
            var from = _dragStartPos;
            var actualTo = _draggingNode.Position;

            bool movedBeyondDeadzone =
                Math.Abs(actualTo.X - from.X) > _settings.DragDeadzone ||
                Math.Abs(actualTo.Y - from.Y) > _settings.DragDeadzone;

            if (movedBeyondDeadzone)
            {
                var to = SnapToGrid(actualTo);
                if (Math.Abs(actualTo.X - to.X) > 0.001 || Math.Abs(actualTo.Y - to.Y) > 0.001)
                    _draggingNode.SetPosition(to.X, to.Y);

                if (!History.IsBusy)
                    History.Record(new MoveNodeCommand(_draggingNode, from, to));
            }
            else
            {
                // Click without drag — snap back to start so no jitter occurs
                _draggingNode.SetPosition(from.X, from.Y);
            }
        }
        _draggingNode = null;
        _dragStartPos = null;
    }

    // ── Pan bounds ────────────────────────────────────────────────────────────

    private void OnPanChanged()
    {
        var pan = Diagram.Pan;
        var zoom = Diagram.Zoom;
        double max = _settings.PanBound * zoom;
        double cx = Math.Clamp(pan.X, -max, max);
        double cy = Math.Clamp(pan.Y, -max, max);
        if (Math.Abs(cx - pan.X) > 0.5 || Math.Abs(cy - pan.Y) > 0.5)
            Diagram.SetPan(cx, cy);
    }

    public void PanTo(double x, double y)
    {
        double max = _settings.PanBound * Diagram.Zoom;
        Diagram.SetPan(Math.Clamp(x, -max, max), Math.Clamp(y, -max, max));
    }

    private Point SnapToGrid(Point point)
    {
        if (!_settings.GridEnabled) return point;

        var grid = Math.Max(1.0, _settings.GridSize);
        return new Point(
            Math.Round(point.X / grid) * grid,
            Math.Round(point.Y / grid) * grid);
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
            else if (model is PetriLinkModel clickedLink && clickedLink != _pendingLink)
                AddVertexToPending(pos);
            else if (model is PetriVertexModel v && v.Parent != _pendingLink)
                AddVertexToPending(pos);
        }
    }

    private void StartPendingLink(Model? model, Point pos)
    {
        PortModel? sourcePort;
        Anchor sourceAnchor;

        if (model is PortModel port)
        {
            sourcePort = port;
            sourceAnchor = MakeAnchor(port.Parent, port);
        }
        else if (model is NodeModel node)
        {
            var snapPort = FindClosestPort(node, pos);
            sourcePort = snapPort;
            sourceAnchor = MakeAnchor(node, snapPort);
        }
        else return;

        _pendingSourcePort = sourcePort;
        _pendingFloating = new MutablePositionAnchor(pos);
        _pendingLink = new PetriLinkModel(sourceAnchor, _pendingFloating)
        {
            Segmentable = false,
            TargetMarker = LinkMarker.Arrow,
            Color = _settings.ArcPendingColor,
            SelectedColor = _settings.ArcPendingColor,
            IsDraggingEndpoint = true,
        };

        Diagram.Links.Add(_pendingLink);
        PendingLinkChanged?.Invoke();
    }

    public void AddVertexToPending(Point pos)
    {
        if (_pendingLink == null || _pendingFloating == null) return;
        _pendingLink.Vertices.Add(new PetriVertexModel(_pendingLink, pos));
        _pendingLink.Refresh();
    }

    public void AddVertexToLink(PetriLinkModel link, Point diagramPos)
    {
        var pts = GetFullLinkPoints(link);
        if (pts.Count < 2) return;

        int bestSeg = 0;
        double bestD = double.MaxValue;
        for (int i = 0; i < pts.Count - 1; i++)
        {
            double d = DistPtSegSq(diagramPos, pts[i], pts[i + 1]);
            if (d < bestD) { bestD = d; bestSeg = i; }
        }

        int insertAt = Math.Clamp(bestSeg, 0, link.Vertices.Count);
        link.Vertices.Insert(insertAt, new PetriVertexModel(link, diagramPos));
        link.Refresh();
    }

    private void FinishPendingLink(NodeModel targetNode, PortModel? targetPort, Point pos)
    {
        if (_pendingLink == null) return;

        var sourceNode = GetParentNode(_pendingLink.Source);
        if (sourceNode == null || sourceNode.GetType() == targetNode.GetType())
        {
            CancelPendingLink();
            return;
        }

        // Check for existing arcs between the same pair of nodes
        var existingArcs = Diagram.Links.OfType<PetriLinkModel>()
            .Where(o => o != _pendingLink
                     && GetParentNode(o.Source) == sourceNode
                     && GetParentNode(o.Target) == targetNode)
            .ToList();

        if (existingArcs.Count > 0)
        {
            // Multiple arcs only valid for Place→Transition (input arcs)
            bool isPlaceToTransition = sourceNode is PlaceNode && targetNode is TransitionNode;
            if (!isPlaceToTransition || existingArcs.Count >= 2)
            {
                CancelPendingLink();
                return;
            }

            bool hasNormal    = existingArcs.Any(a => a.ArcType == ArcType.Normal);
            bool hasInhibitor = existingArcs.Any(a => a.ArcType == ArcType.Inhibitor);
            bool hasReset     = existingArcs.Any(a => a.ArcType == ArcType.Reset);

            // Valid second-arc combinations: Normal+Inhibitor or Normal+Reset
            // Inhibitor+Reset is not meaningful (semantically contradictory)
            bool pairAlreadyFull = (hasNormal && hasInhibitor)
                                || (hasNormal && hasReset)
                                || (hasInhibitor && hasReset);
            if (pairAlreadyFull) { CancelPendingLink(); return; }

            // Assign the complementary type for the one arc already present
            if (hasInhibitor || hasReset)
                _pendingLink.ArcType = ArcType.Normal;
            else // hasNormal — offer Inhibitor as default second arc
                _pendingLink.ArcType = ArcType.Inhibitor;
        }

        Anchor targetAnchor = MakeAnchor(targetNode, targetPort);

        _pendingLink.SetTarget(targetAnchor);
        _pendingLink.IsDraggingEndpoint = false;
        _pendingLink.Color = _settings.ArcColor;
        _pendingLink.CanonicalSourceId = sourceNode.Id;

        var finalised = _pendingLink;
        _pendingLink = null;
        _pendingFloating = null;
        _pendingSourcePort = null;

        finalised.Refresh();
        AddControls(finalised);
        History.Record(new AddLinkCommand(Diagram, NodeRegistry, finalised, Logger, _settings));
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

    // ── Endpoint drag (reconnect) ─────────────────────────────────────────────

    public void StartEndpointDrag(PetriLinkModel link, bool isSource, double clientX, double clientY)
    {
        var beforeSnapshot = LinkHelpers.Snapshot(link);
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
        tempLink.Color = _settings.ArcColor;
        tempLink.SelectedColor = _settings.ArcSelectedColor;
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

            // Also allow dropping back on the dragged end's original node
            var originalNode = GetParentNode(fixedIsSource ? snapshotTarget : snapshotSource);
            if (hitNode == null && originalNode != null && HitTest(originalNode, dropPos))
                hitNode = originalNode;

            if (hitNode == null)
            {
                Diagram.Links.Remove(tempLink);
                RestoreLinkDirect(snapshotSource, snapshotTarget, weight, arcType, canonicalSourceId, vertexPositions);
                return;
            }

            var snapPort = FindClosestPort(hitNode, dropPos);
            Anchor nodeAnchor = MakeAnchor(hitNode, snapPort);

            if (fixedIsSource) tempLink.SetTarget(nodeAnchor);
            else tempLink.SetSource(nodeAnchor);

            tempLink.SnapshotSource = null;
            tempLink.SnapshotTarget = null;
            tempLink.Refresh();

            var srcNode = GetParentNode(tempLink.Source);
            var tgtNode = GetParentNode(tempLink.Target);
            if (srcNode != null && tgtNode != null && ValidatePetriLink(tempLink, srcNode, tgtNode))
            {
                AddControls(tempLink);
                var afterSnapshot = LinkHelpers.Snapshot(tempLink);
                if (beforeSnapshot.SourceNodeId != afterSnapshot.SourceNodeId ||
                    beforeSnapshot.TargetNodeId != afterSnapshot.TargetNodeId ||
                    beforeSnapshot.Weight != afterSnapshot.Weight ||
                    beforeSnapshot.ArcType != afterSnapshot.ArcType ||
                    beforeSnapshot.WeightLabelSegment != afterSnapshot.WeightLabelSegment ||
                    beforeSnapshot.WeightLabelFlipped != afterSnapshot.WeightLabelFlipped ||
                    beforeSnapshot.VertexPositions.Count != afterSnapshot.VertexPositions.Count ||
                    beforeSnapshot.VertexPositions.Where((t, i) => t.X != afterSnapshot.VertexPositions[i].X || t.Y != afterSnapshot.VertexPositions[i].Y).Any())
                {
                    History.Record(new ReconnectLinkCommand(Diagram, NodeRegistry, beforeSnapshot, afterSnapshot, Logger, _settings));
                }
            }
        }

        Diagram.PointerMove += OnMove;
        Diagram.PointerUp += OnUp;
    }

    private void RestoreLinkDirect(Anchor src, Anchor tgt, int weight, ArcType arcType,
                                   string? canonicalSourceId, List<Point> vertices)
    {
        var link = new PetriLinkModel(src, tgt)
        {
            Segmentable = false,
            TargetMarker = LinkMarker.Arrow,
            Color = _settings.ArcColor,
            SelectedColor = _settings.ArcSelectedColor,
            Weight = weight,
            ArcType = arcType,
            CanonicalSourceId = canonicalSourceId,
        };
        foreach (var vp in vertices)
            link.Vertices.Add(new PetriVertexModel(link, vp));
        Diagram.Links.Add(link);
    }

    /// <summary>
    /// Adds a link between two already-registered nodes (for use during diagram restore).
    /// </summary>
    public void RestoreLink(NodeModel source, NodeModel target, int weight, ArcType arcType,
                            IEnumerable<Point> vertices)
    {
        var src = MakeAnchor(source, null);
        var tgt = MakeAnchor(target, null);
        RestoreLinkDirect(src, tgt, weight, arcType, null, vertices.ToList());
    }

    // ── Link lifecycle ────────────────────────────────────────────────────────

    private void OnLinkAdded(BaseLinkModel baseLink)
    {
        if (baseLink is not PetriLinkModel link) return;
        if (link.IsDraggingEndpoint) return;
        AddControls(link);
        link.TargetAttached += OnLinkTargetAttached;
    }

    private void OnLinkRemoved(BaseLinkModel baseLink)
    {
        if (baseLink is not PetriLinkModel link) return;
        link.TargetAttached -= OnLinkTargetAttached;
    }

    private void AddControls(PetriLinkModel link) { }

    private async void OnLinkTargetAttached(BaseLinkModel baseLink)
    {
        try
        {
            if (baseLink is not PetriLinkModel link) return;
            if (link.IsDraggingEndpoint) return;
            await Task.Yield();
            if (History.IsBusy) return;
            var src = GetParentNode(link.Source);
            var tgt = GetParentNode(link.Target);
            if (src == null || tgt == null) return;
            if (ValidatePetriLink(link, src, tgt))
                link.CanonicalSourceId ??= src.Id;
        }
        catch (Exception ex)
        {
            Logger.Log("LinkAttach", $"EXCEPTION {ex.GetType().Name}: {ex.Message}");
        }
    }

    private bool ValidatePetriLink(PetriLinkModel link, NodeModel src, NodeModel tgt)
    {
        if (src.GetType() == tgt.GetType()) { Diagram.Links.Remove(link); return false; }

        var siblings = Diagram.Links.OfType<PetriLinkModel>()
            .Where(o => o != link && GetParentNode(o.Source) == src && GetParentNode(o.Target) == tgt)
            .ToList();

        if (siblings.Count == 0) return true;

        // Multiple arcs only valid for Place→Transition
        if (src is not PlaceNode || tgt is not TransitionNode) { Diagram.Links.Remove(link); return false; }
        // Max 2 arcs
        if (siblings.Count >= 2) { Diagram.Links.Remove(link); return false; }
        // No duplicate arc type
        if (siblings.Any(s => s.ArcType == link.ArcType)) { Diagram.Links.Remove(link); return false; }
        // Inhibitor+Reset pair is invalid
        bool sibHasInhibitor = siblings.Any(s => s.ArcType == ArcType.Inhibitor);
        bool sibHasReset     = siblings.Any(s => s.ArcType == ArcType.Reset);
        if ((link.ArcType == ArcType.Inhibitor && sibHasReset) ||
            (link.ArcType == ArcType.Reset && sibHasInhibitor)) { Diagram.Links.Remove(link); return false; }

        return true;
    }

    // ── Node management ───────────────────────────────────────────────────────

    private string NextPlaceName()
    {
        var used = Diagram.Nodes.OfType<PlaceNode>().Select(p => p.Data.Name).ToHashSet();
        while (used.Contains($"P{_placeCounter}")) _placeCounter++;
        return $"P{_placeCounter++}";
    }

    private string NextTransitionName()
    {
        var used = Diagram.Nodes.OfType<TransitionNode>().Select(t => t.Data.Name).ToHashSet();
        while (used.Contains($"T{_transitionCounter}")) _transitionCounter++;
        return $"T{_transitionCounter++}";
    }

    public void AddNodeAt(string type, Point clientPoint)
    {
        var point = SnapToGrid(Diagram.GetRelativeMousePoint(clientPoint.X, clientPoint.Y));

        IDiagramCommand? cmd = null;
        string? addedId = null;

        if (type == "place")
        {
            var place = new Place { Name = NextPlaceName() };
            var node = new PlaceNode(place, _settings);
            node.SetPosition(
                point.X - (node.Size?.Width ?? _settings.PlaceSize) / 2.0,
                point.Y - (node.Size?.Height ?? _settings.PlaceSize) / 2.0);
            addedId = place.Id;
            cmd = new AddNodeCommand(Diagram, node, Logger, _settings);
        }
        else if (type == "transition")
        {
            var transition = new Transition { Name = NextTransitionName() };
            var node = new TransitionNode(transition, _settings);
            node.SetPosition(
                point.X - (node.Size?.Width ?? _settings.TransitionWidth) / 2.0,
                point.Y - (node.Size?.Height ?? _settings.TransitionHeight) / 2.0);
            addedId = transition.Id;
            cmd = new AddNodeCommand(Diagram, node, Logger, _settings);
        }

        if (cmd is null) return;

        History.Execute(cmd);

        if (addedId != null)
        {
            var added = NodeRegistry.Find(addedId);
            if (added != null) Diagram.SelectModel(added, true);
        }
    }

    public void DeleteSelected()
    {
        var selected = Diagram.GetSelectedModels().ToList();
        Logger.Log("DeleteSel", $"count={selected.Count} IsBusy={History.IsBusy}");
        if (selected.Count == 0) return;

        var seenIds = new HashSet<string>();
        var nodesToDelete = selected
            .OfType<NodeModel>()
            .Where(n => { var id = GetDomainId(n); return id != null && seenIds.Add(id!); })
            .ToHashSet();

        var linkSnapsByNode = nodesToDelete.ToDictionary(
            n => n,
            n => Diagram.Links
                .OfType<PetriLinkModel>()
                .Where(l => !l.IsDraggingEndpoint
                         && l.Target != null
                         && (GetParentNode(l.Source) == n || GetParentNode(l.Target) == n))
                .Select(LinkHelpers.Snapshot)
                .ToList());

        var standaloneLinks = selected
            .OfType<PetriLinkModel>()
            .Where(l => !l.IsDraggingEndpoint && l.Target != null)
            .Where(l => !nodesToDelete.Contains(GetParentNode(l.Source))
                     && !nodesToDelete.Contains(GetParentNode(l.Target)))
            .ToList();

        var removedArcIds = new HashSet<(string, string)>(
            nodesToDelete
                .SelectMany(n => linkSnapsByNode[n])
                .Concat(standaloneLinks.Select(LinkHelpers.Snapshot))
                .Select(s => (s.SourceNodeId, s.TargetNodeId)));

        var cmds = new List<IDiagramCommand>();

        foreach (var model in selected)
        {
            if (model is PetriVertexModel v && v.Parent is PetriLinkModel parentLink)
            {
                var srcId = GetDomainId(GetParentNode(parentLink.Source)) ?? "";
                var tgtId = GetDomainId(GetParentNode(parentLink.Target)) ?? "";
                if (!removedArcIds.Contains((srcId, tgtId)))
                    cmds.Add(new RemoveVertexCommand(parentLink, v));
            }
        }

        foreach (var link in standaloneLinks)
            cmds.Add(new RemoveLinkCommand(Diagram, NodeRegistry, link, Logger, _settings));

        foreach (var node in nodesToDelete)
        {
            IDiagramCommand nodeCmd = node switch
            {
                PlaceNode pn => new RemovePlaceCommand(Diagram, NodeRegistry, pn, linkSnapsByNode[pn], Logger, _settings),
                TransitionNode tn => new RemoveTransitionCommand(Diagram, NodeRegistry, tn, linkSnapsByNode[tn], Logger, _settings),
                _ => throw new InvalidOperationException($"Unknown node type: {node.GetType().Name}")
            };
            cmds.Add(nodeCmd);
        }

        Logger.Log("DeleteSel", $"cmds={cmds.Count} [{string.Join(", ", cmds.Select(c => c.GetType().Name))}]");
        if (cmds.Count == 0) return;

        var composite = new CompositeCommand(cmds, Logger);

        History.BeginBatch();
        try
        {
            Diagram.UnselectAll();
            composite.Execute();
        }
        catch (Exception ex)
        {
            Logger.Log("DeleteSel", $"EXCEPTION {ex.GetType().Name}: {ex.Message}");
            throw;
        }
        finally
        {
            History.EndBatch();
        }

        History.Record(composite);
    }

    public void HandleDoubleClick(Model? model, Point clientPoint) { }

    // ── View ──────────────────────────────────────────────────────────────────

    public void ZoomIn() => Diagram.SetZoom(Math.Min(Diagram.Zoom * _settings.ZoomStep, _settings.ZoomMax));
    public void ZoomOut() => Diagram.SetZoom(Math.Max(Diagram.Zoom / _settings.ZoomStep, _settings.ZoomMin));
    public void ResetView() { Diagram.SetPan(0, 0); Diagram.SetZoom(1); }

    // ── Layout helpers ────────────────────────────────────────────────────────

    public IReadOnlyList<NodeModel> GetAllNodes() =>
        Diagram.Nodes.OfType<PlaceNode>().Cast<NodeModel>()
               .Concat(Diagram.Nodes.OfType<TransitionNode>())
               .ToList();

    public IReadOnlyList<(string From, string To)> GetEdgePairs() =>
        Diagram.Links
               .OfType<PetriLinkModel>()
               .Where(l => !l.IsDraggingEndpoint && l.Target != null)
               .Select(l => (GetDomainId(GetParentNode(l.Source)) ?? "",
                             GetDomainId(GetParentNode(l.Target)) ?? ""))
               .Where(p => p.Item1 != "" && p.Item2 != "")
               .ToList();

    public IReadOnlyList<(PetriLinkModel Link, string SrcId, string TgtId)> GetLinkTriples() =>
        Diagram.Links
               .OfType<PetriLinkModel>()
               .Where(l => !l.IsDraggingEndpoint && l.Target != null)
               .Select(l => (l,
                   GetDomainId(GetParentNode(l.Source)) ?? "",
                   GetDomainId(GetParentNode(l.Target)) ?? ""))
               .Where(t => t.Item2 != "" && t.Item3 != "")
               .ToList();

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string? GetDomainId(NodeModel? node) => node switch
    {
        PlaceNode pn => pn.Data.Id,
        TransitionNode tn => tn.Data.Id,
        _ => null
    };

    private NodeModel? GetParentNode(Anchor anchor) => anchor.Model switch
    {
        NodeModel n => n,
        PortModel pm => pm.Parent,
        _ => null
    };

    public const double PortSnapRadius = 8.0;

    /// <summary>Returns the closest port if it is within <see cref="PortSnapRadius"/>, otherwise null.</summary>
    public PortModel? FindClosestPort(NodeModel node, Point p)
    {
        if (!node.Ports.Any()) return null;
        var closest = node.Ports.OrderBy(port =>
        {
            var pp = port.Position;
            if (pp == null) return double.MaxValue;
            double dx = pp.X - p.X, dy = pp.Y - p.Y;
            return dx * dx + dy * dy;
        }).First();

        var cp = closest.Position;
        if (cp == null) return null;
        double ddx = cp.X - p.X, ddy = cp.Y - p.Y;
        return ddx * ddx + ddy * ddy <= PortSnapRadius * PortSnapRadius ? closest : null;
    }

    /// <summary>
    /// Creates the right anchor for a node+port pair.
    /// PlaceNodes with a port use PortCircleAnchor (fixed direction on circle edge);
    /// PlaceNodes without a port use EdgeIntersectionAnchor (dynamic circle intersection);
    /// TransitionNodes use SinglePortAnchor when a port is available, else EdgeIntersectionAnchor.
    /// </summary>
    private static Anchor MakeAnchor(NodeModel node, PortModel? port)
    {
        if (node is PlaceNode place)
            return port != null
                ? (Anchor)new PortCircleAnchor(place, port)
                : new EdgeIntersectionAnchor(node);
        if (port != null)
            return new SinglePortAnchor(port) { MiddleIfNoMarker = false, UseShapeAndAlignment = true };
        return new EdgeIntersectionAnchor(node);
    }

    private LinkModel? LinkFactory(Diagram diagram, ILinkable source, Anchor? targetAnchor)
    {
        Anchor? src = source switch
        {
            PortModel pm => MakeAnchor(pm.Parent, pm),
            NodeModel n => MakeAnchor(n, null),
            _ => null
        };
        if (src is null) return null;
        return new PetriLinkModel(src, targetAnchor)
        {
            Segmentable = false,
            TargetMarker = LinkMarker.Arrow,
            Color = _settings.ArcColor,
            SelectedColor = _settings.ArcSelectedColor,
        };
    }

    private List<Point> GetFullLinkPoints(PetriLinkModel link)
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

    private bool HitTest(NodeModel node, Point p)
    {
        if (node.Position == null || node.Size == null) return false;
        double pad = _settings.EndpointDragHitPadding;
        return p.X >= node.Position.X - pad
            && p.X <= node.Position.X + node.Size.Width + pad
            && p.Y >= node.Position.Y - pad
            && p.Y <= node.Position.Y + node.Size.Height + pad;
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

    // ── Dispose ───────────────────────────────────────────────────────────────

    public void Dispose()
    {
        _settings.Changed -= OnSettingsChanged;
        Diagram.Links.Added -= OnLinkAdded;
        Diagram.Links.Removed -= OnLinkRemoved;
        Diagram.PointerMove -= OnPointerMove;
        Diagram.PanChanged -= OnPanChanged;
        Diagram.Nodes.Added -= OnNodeAdded;
        Diagram.Nodes.Removed -= OnNodeRemoved;
        Diagram.PointerDown -= OnDiagramPointerDown;
        Diagram.PointerUp -= OnDiagramPointerUp;
        foreach (var link in Diagram.Links.OfType<PetriLinkModel>())
            link.TargetAttached -= OnLinkTargetAttached;
    }
}