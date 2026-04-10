using Blazor.Diagrams.Core.Anchors;
using Blazor.Diagrams.Core.Geometry;
using Blazor.Diagrams.Core.Models;
using Blazor.Diagrams.Core.Models.Base;

namespace PetriNetAnalyzer.DiagramModels;

/// <summary>
/// Anchor that places the endpoint exactly on the node's visible edge:
/// • PlaceNode  → circle, radius = Size.Width / 2
/// • TransitionNode → rectangle
/// The intersection is computed from the line between the two node centres.
/// </summary>
public sealed class EdgeIntersectionAnchor : Anchor
{
    private readonly NodeModel _node;

    public EdgeIntersectionAnchor(NodeModel node) : base(node)
    {
        _node = node;
    }

    public override Point? GetPosition(BaseLinkModel link, Point[] route)
    {
        // Find the opposite endpoint to aim at
        var opposite = GetOppositeCenter(link);
        if (opposite == null) return GetPlainPosition();
        return Intersect(_node, opposite);
    }

    public override Point? GetPlainPosition() => NodeCenter(_node);

    // ── Helpers ──────────────────────────────────────────────────────────────

    private Point? GetOppositeCenter(BaseLinkModel link)
    {
        bool iAmSource = ReferenceEquals(link.Source.Model, _node);
        var other = iAmSource ? link.Target : link.Source;
        if (other?.Model is NodeModel otherNode) return NodeCenter(otherNode);
        if (other?.Model is PortModel pm && pm.Parent is NodeModel pn) return NodeCenter(pn);
        return null;
    }

    private static Point? NodeCenter(NodeModel n)
    {
        var p = n.Position;
        var s = n.Size;
        if (p == null || s == null) return null;
        return new Point(p.X + s.Width / 2.0, p.Y + s.Height / 2.0);
    }

    private static Point? Intersect(NodeModel node, Point target)
    {
        var center = NodeCenter(node);
        if (center == null) return null;

        double dx = target.X - center.X;
        double dy = target.Y - center.Y;
        double len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 0.001) return center;

        double ux = dx / len, uy = dy / len;

        if (node is PlaceNode place)
        {
            // Circle: move from centre outward by radius
            double r = (place.Size?.Width ?? 60.0) / 2.0;
            return new Point(center.X + ux * r, center.Y + uy * r);
        }
        else
        {
            // Rectangle: find ray–rect intersection
            var s = node.Size;
            if (s == null) return center;
            double hw = s.Width  / 2.0;
            double hh = s.Height / 2.0;

            // Ray from center in direction (ux,uy); find t where it hits each face
            double tMin = double.MaxValue;

            if (Math.Abs(ux) > 1e-9)
            {
                double t = (ux > 0 ? hw : -hw) / ux;
                if (t > 0 && Math.Abs(uy * t) <= hh) tMin = Math.Min(tMin, t);
            }
            if (Math.Abs(uy) > 1e-9)
            {
                double t = (uy > 0 ? hh : -hh) / uy;
                if (t > 0 && Math.Abs(ux * t) <= hw) tMin = Math.Min(tMin, t);
            }

            if (tMin == double.MaxValue) return center;
            return new Point(center.X + ux * tMin, center.Y + uy * tMin);
        }
    }
}
