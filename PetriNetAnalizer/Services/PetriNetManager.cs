using Blazor.Diagrams;
using Blazor.Diagrams.Core;
using Blazor.Diagrams.Core.Anchors;
using Blazor.Diagrams.Core.Controls.Default;
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

    private int _placeCounter = 1;
    private int _transitionCounter = 1;

    public PetriNetManager()
    {
        var options = new BlazorDiagramOptions
        {
            AllowMultiSelection = false,
            Virtualization = { Enabled = false },
            Links = {
                DefaultRouter = new NormalRouter(),
                DefaultPathGenerator = new StraightPathGenerator(radius: 20),
                Factory = LinkFactory,
                SnappingRadius = 3,            
            }
        };

        Diagram = new BlazorDiagram(options);
        Diagram.RegisterComponent<PlaceNode, PlaceComponent>();
        Diagram.RegisterComponent<TransitionNode, TransitionComponent>();
        Diagram.RegisterComponent<PetriVertexModel, PetriVertexWidget>();

        Diagram.Links.Added += OnLinkAdded;

        Diagram.PointerDown += (model, args) =>
        {

            if (model is LinkModel link)
            {
                Diagram.SelectModel(link, true);
                link.Refresh();
            }
            else if (model is LinkVertexModel vertex && vertex.Parent is LinkModel parentLink)
            {
                Diagram.SelectModel(parentLink, true);
                parentLink.Refresh();
            }
        };
    }

    private void OnLinkAdded(BaseLinkModel baseLink)
    {
        if (baseLink is not LinkModel link) return;

        link.TargetChanged += (l, oldAnchor, newAnchor) =>
        {
            CheckPetriRule((LinkModel)l);
        };

        var diamondMarker = new LinkMarker("M 7.6569 -5.6569 L 13.3137 0 L 7.6569 5.6569 L 2 0 Z", 11.31370000001);
        Diagram.Controls.AddFor(link)
        .Add(new PetriArrowControl(source: true, marker: diamondMarker))
        .Add(new PetriArrowControl(source: false, marker: diamondMarker));

        link.TargetAttached += async (m) =>
        {
            await Task.Yield();
            NormalizePetriLink(link);
        };

    }
    private void NormalizePetriLink(LinkModel link)
    {
        if (link is not PetriLinkModel petriLink) return;

        if (link.Source is SinglePortAnchor s && link.Target is SinglePortAnchor t)
        {
            if (petriLink.IsAdjustingSource)
            {
                ReverseLinkCompletely(petriLink);

                petriLink.IsAdjustingSource = false;
            }
        }
    }

    private void ReverseLinkCompletely(LinkModel link)
    {
        var s = link.Source;
        var t = link.Target;

        link.SetSource(t);
        link.SetTarget(s);

        if (link.Vertices.Count > 0)
        {
            var rev = link.Vertices.AsEnumerable().Reverse().ToList();
            link.Vertices.Clear();
            foreach (var v in rev) link.Vertices.Add(v);
        }

        link.Refresh();
    }
    private void CheckPetriRule(LinkModel link)
    {
        if (link.Target.Model == null) return;

        var sourceNode = GetParentNode(link.Source);
        var targetNode = GetParentNode(link.Target);

        if (sourceNode == null || targetNode == null) return;

        if (sourceNode.GetType() == targetNode.GetType())
        {
            Diagram.Links.Remove(link);
        }

        if (sourceNode.GetType() == targetNode.GetType())
        {
            Diagram.Links.Remove(link);
            return; 
        }

        var duplicateExists = Diagram.Links
            .OfType<LinkModel>()
            .Any(otherLink =>
                otherLink != link && 
                GetParentNode(otherLink.Source) == sourceNode &&
                GetParentNode(otherLink.Target) == targetNode);


        if (link is not PetriLinkModel petriLink) return;

        if (duplicateExists && !petriLink.IsAdjustingSource)
        {
            Diagram.Links.Remove(link);
        }
    }

    private NodeModel? GetParentNode(Anchor anchor)
    {
        if (anchor.Model is NodeModel node) return node;
        if (anchor.Model is PortModel port) return port.Parent;
        return null;
    }
    private LinkModel LinkFactory(Diagram diagram, ILinkable source, Anchor? targetAnchor)
    {

        Anchor sourceAnchor;

        if (source is NodeModel node)
        {
            sourceAnchor = new ShapeIntersectionAnchor(node);
        }
        else if (source is PortModel port)
        {
            sourceAnchor = new SinglePortAnchor(port)
            {
                MiddleIfNoMarker = false,
                UseShapeAndAlignment = true
            };
        }
        else
        {
            return null!;
        }

        var link = new PetriLinkModel(sourceAnchor, targetAnchor)
        {
            Segmentable = false,
            TargetMarker = LinkMarker.Arrow,
            Color = "black",
            SelectedColor = "#007bff",

        };

        return link;
    }

    public void AddNodeAt(string type, Point clientPoint)
    {
        var point = Diagram.GetRelativeMousePoint(clientPoint.X, clientPoint.Y);
        NodeModel? newNode = type switch
        {
            "place" => new PlaceNode(new Place { Name = $"P{_placeCounter++}" }),
            "transition" => new TransitionNode(new Transition { Name = $"T{_transitionCounter++}" }),
            _ => null
        };

        if (newNode != null)
        {
            var centerX = point.X - (newNode.Size?.Width ?? 60) / 2;
            var centerY = point.Y - (newNode.Size?.Height ?? 60) / 2;
            newNode.Position = new Point(centerX, centerY);
            Diagram.Nodes.Add(newNode);
        }
    }

    public void DeleteSelected()
    {
        var selectedModels = Diagram.GetSelectedModels().ToList();
        foreach (var model in selectedModels)
        {
            if (model is PetriVertexModel vertex)
            {
                var link = vertex.Parent;
                link.Vertices.Remove(vertex);
                link.Refresh();
            }
            else if (model is NodeModel node) Diagram.Nodes.Remove(node);
            else if (model is LinkModel link) Diagram.Links.Remove(link);
        }
    }

    public void HandleDoubleClick(Model? model, Point clientPoint)
    {
        if (model is LinkModel link)
        {
            var relPoint = Diagram.GetRelativeMousePoint(clientPoint.X, clientPoint.Y);
            var points = GetFullLinkPoints(link, relPoint);
            var segIndex = FindClosestSegmentIndex(points, relPoint);

            var newVertex = new PetriVertexModel(link, relPoint);
            link.Vertices.Insert(Math.Clamp(segIndex, 0, link.Vertices.Count), newVertex);

            Diagram.SelectModel(link, true);
            Diagram.SelectModel(newVertex, true);
        }
        else if (model is PetriVertexModel vertex)
        {
            vertex.Parent.Vertices.Remove(vertex);
        }
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
        var bestIndex = 0;
        var bestDist2 = double.MaxValue;
        for (var i = 0; i < pts.Count - 1; i++)
        {
            var d2 = DistancePointToSegmentSquared(p, pts[i], pts[i + 1]);
            if (d2 < bestDist2) { bestDist2 = d2; bestIndex = i; }
        }
        return bestIndex;
    }

    private static double DistancePointToSegmentSquared(Point p, Point a, Point b)
    {
        var abx = b.X - a.X; var aby = b.Y - a.Y;
        var apx = p.X - a.X; var apy = p.Y - a.Y;
        var abLen2 = abx * abx + aby * aby;
        if (abLen2 < 1e-9) return apx * apx + apy * apy;
        var t = Math.Max(0, Math.Min(1, (apx * abx + apy * aby) / abLen2));
        var dx = p.X - (a.X + t * abx);
        var dy = p.Y - (a.Y + t * aby);
        return dx * dx + dy * dy;
    }
    public void Dispose()
    {
        Diagram.Links.Added -= OnLinkAdded;
    }
}