using Blazor.Diagrams.Core;
using Blazor.Diagrams.Core.Anchors;
using Blazor.Diagrams.Core.Controls;
using Blazor.Diagrams.Core.Events;
using Blazor.Diagrams.Core.Geometry;
using Blazor.Diagrams.Core.Models;
using Blazor.Diagrams.Core.Models.Base;
using Core.Models;
using PetriNetAnalyzer.Services;

namespace PetriNetAnalyzer.DiagramModels
{
    public class PetriEndpointControl : ExecutableControl
    {
        private readonly bool _isSourceEnd;
        private readonly Action<PetriLinkModel, NodeModel, NodeModel> _validatePetriLink;
        private readonly Action<PetriLinkModel> _addControls;
        private readonly PetriNetManager _manager;

        public PetriEndpointControl(bool isSourceEnd,
            Action<PetriLinkModel, NodeModel, NodeModel> validatePetriLink,
            Action<PetriLinkModel> addControls,
            PetriNetManager manager)
        {
            _isSourceEnd = isSourceEnd;
            _validatePetriLink = validatePetriLink;
            _addControls = addControls;
            _manager = manager;
        }

        public override Point? GetPosition(Model model)
        {
            if (model is not PetriLinkModel link) return null;
            Point hint;
            if (_isSourceEnd)
            {
                hint = link.Target.GetPlainPosition() ?? new Point(0, 0);
                return link.Source.GetPosition(link, new[] { hint });
            }
            else
            {
                hint = link.Source.GetPlainPosition() ?? new Point(0, 0);
                return link.Target.GetPosition(link, new[] { hint });
            }
        }

        public void StartDrag(Diagram diagram, Model model, double clientX, double clientY)
        {
            if (model is not PetriLinkModel link) return;

            var snapshotSource = link.Source;
            var snapshotTarget = link.Target;
            var weight = link.Weight;
            var arcType = link.ArcType;          // ← preserve arc type
            var canonicalSourceId = link.CanonicalSourceId;
            var vertexPositions = link.Vertices.Select(v => v.Position).ToList();

            var fixedAnchor = _isSourceEnd ? link.Target : link.Source;
            var fixedIsSource = !_isSourceEnd;

            diagram.Links.Remove(link);

            var mousePos = diagram.GetRelativeMousePoint(clientX, clientY);
            var floatingAnchor = new MutablePositionAnchor(mousePos);

            PetriLinkModel tempLink;
            if (fixedIsSource)
                tempLink = new PetriLinkModel(fixedAnchor, floatingAnchor) { TargetMarker = LinkMarker.Arrow };
            else
                tempLink = new PetriLinkModel(floatingAnchor, fixedAnchor) { TargetMarker = LinkMarker.Arrow };

            tempLink.Segmentable = false;
            tempLink.Color = "black";
            tempLink.SelectedColor = "#007bff";
            tempLink.Weight = weight;
            tempLink.ArcType = arcType;        // ← restore arc type
            tempLink.CanonicalSourceId = canonicalSourceId;
            tempLink.IsDraggingEndpoint = true;
            tempLink.SnapshotSource = snapshotSource;
            tempLink.SnapshotTarget = snapshotTarget;

            foreach (var vp in vertexPositions)
                tempLink.Vertices.Add(new PetriVertexModel(tempLink, vp));

            diagram.Links.Add(tempLink);

            void OnMove(Model? _, PointerEventArgs me)
            {
                floatingAnchor.SetPosition(diagram.GetRelativeMousePoint(me.ClientX, me.ClientY));
                tempLink.Refresh();
            }

            void OnUp(Model? _, PointerEventArgs ue)
            {
                diagram.PointerMove -= OnMove;
                diagram.PointerUp -= OnUp;

                tempLink.IsDraggingEndpoint = false;

                var dropPos = diagram.GetRelativeMousePoint(ue.ClientX, ue.ClientY);
                var fixedNode = GetNode(fixedAnchor);

                var hitNode = diagram.Nodes
                    .FirstOrDefault(n => n != fixedNode && HitTest(n, dropPos));

                if (hitNode == null)
                {
                    // Dropped on canvas — restore original link unchanged
                    diagram.Links.Remove(tempLink);
                    RestoreOriginal(diagram, snapshotSource, snapshotTarget,
                        weight, arcType, canonicalSourceId, vertexPositions);
                    return;
                }

                var closestPort = _manager.FindClosestPort(hitNode, dropPos);
                Anchor nodeAnchor = closestPort != null
                    ? (Anchor)new SinglePortAnchor(closestPort) { MiddleIfNoMarker = false, UseShapeAndAlignment = true }
                    : new ShapeIntersectionAnchor(hitNode);

                if (fixedIsSource)
                    tempLink.SetTarget(nodeAnchor);
                else
                    tempLink.SetSource(nodeAnchor);

                tempLink.SnapshotSource = null;
                tempLink.SnapshotTarget = null;
                tempLink.Refresh();

                var srcNode = GetNode(tempLink.Source);
                var tgtNode = GetNode(tempLink.Target);
                if (srcNode != null && tgtNode != null)
                {
                    _validatePetriLink(tempLink, srcNode, tgtNode);
                    if (diagram.Links.Contains(tempLink))
                        _addControls(tempLink);
                }
            }

            diagram.PointerMove += OnMove;
            diagram.PointerUp += OnUp;
        }

        public override ValueTask OnPointerDown(Diagram diagram, Model model, PointerEventArgs e)
            => ValueTask.CompletedTask;

        private static void RestoreOriginal(Diagram diagram,
            Anchor src, Anchor tgt, int weight, ArcType arcType,
            string? canonicalSourceId, List<Point> vertices)
        {
            var restored = new PetriLinkModel(src, tgt)
            {
                Segmentable = false,
                TargetMarker = LinkMarker.Arrow,
                Color = "black",
                SelectedColor = "#007bff",
                Weight = weight,
                ArcType = arcType,              // ← preserve arc type
                CanonicalSourceId = canonicalSourceId,
            };
            foreach (var vp in vertices)
                restored.Vertices.Add(new PetriVertexModel(restored, vp));
            diagram.Links.Add(restored);
        }

        private static NodeModel? GetNode(Anchor anchor) => anchor.Model switch
        {
            NodeModel n => n,
            PortModel pm => pm.Parent,
            _ => null
        };

        private static bool HitTest(NodeModel node, Point p)
        {
            if (node.Position == null || node.Size == null) return false;
            const double snap = 12;
            return p.X >= node.Position.X - snap &&
                   p.X <= node.Position.X + node.Size.Width + snap &&
                   p.Y >= node.Position.Y - snap &&
                   p.Y <= node.Position.Y + node.Size.Height + snap;
        }
    }
}