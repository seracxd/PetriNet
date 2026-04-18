using Blazor.Diagrams.Core.Anchors;
using Blazor.Diagrams.Core.Geometry;
using Blazor.Diagrams.Core.Models;
using Blazor.Diagrams.Core.Models.Base;

namespace PetriNetAnalyzer.DiagramModels;

/// <summary>
/// Anchor for a PlaceNode that fixes the connection to a specific port's direction,
/// but projects the endpoint onto the circle edge rather than the bounding-box edge.
/// The direction is derived from the port's position relative to the node center,
/// so the arc endpoint stays on the circle at the chosen port's side even when other
/// nodes are moved.
/// </summary>
public sealed class PortCircleAnchor : Anchor
{
    private readonly PlaceNode _place;
    private readonly PortModel _port;

    public PortCircleAnchor(PlaceNode place, PortModel port) : base(port)
    {
        _place = place;
        _port  = port;
    }

    public override Point? GetPosition(BaseLinkModel link, Point[] route)
    {
        // Use the port's own position as the "target direction" point —
        // this gives a fixed direction that doesn't slide when other nodes move.
        var portPos = _port.Position;
        if (portPos == null) return GetPlainPosition();

        var nodePos = _place.Position;
        var nodeSize = _place.Size;
        if (nodePos == null || nodeSize == null) return GetPlainPosition();

        double cx = nodePos.X + nodeSize.Width  / 2.0;
        double cy = nodePos.Y + nodeSize.Height / 2.0;
        double r  = nodeSize.Width / 2.0;

        double dx = portPos.X - cx;
        double dy = portPos.Y - cy;
        double len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 0.001) return new Point(cx, cy - r); // fallback: top of circle

        return new Point(cx + dx / len * r, cy + dy / len * r);
    }

    public override Point? GetPlainPosition()
    {
        var nodePos  = _place.Position;
        var nodeSize = _place.Size;
        if (nodePos == null || nodeSize == null) return null;
        return new Point(nodePos.X + nodeSize.Width / 2.0, nodePos.Y + nodeSize.Height / 2.0);
    }
}
