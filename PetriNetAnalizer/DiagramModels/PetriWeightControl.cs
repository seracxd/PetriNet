using Blazor.Diagrams.Core.Controls;
using Blazor.Diagrams.Core.Geometry;
using Blazor.Diagrams.Core.Models;
using Blazor.Diagrams.Core.Models.Base;
using PetriNetAnalyzer.DiagramModels;

namespace PetriNetAnalyzer.Components.Widgets;

public class PetriWeightControl : Control
{
    public override Point? GetPosition(Model model)
    {
        if (model is not PetriLinkModel link)
            return null;

        var pts = GetFullLinkPoints(link);
        if (pts.Count == 0)
            return null;

        Point mid;
        if (pts.Count == 1)
            mid = pts[0];
        else
        {
            var seg = (pts.Count - 2) / 2;
            var a = pts[seg];
            var b = pts[seg + 1];
            mid = new Point((a.X + b.X) / 2, (a.Y + b.Y) / 2);
        }

        return mid + link.WeightLabelOffset;
    }

    public static List<Point> GetFullLinkPoints(LinkModel link)
    {
        var fallback = link.Vertices.Count > 0 ? link.Vertices[0].Position : new Point(0, 0);

        var first = link.Vertices.Count > 0 ? link.Vertices[0].Position : fallback;
        var last = link.Vertices.Count > 0 ? link.Vertices[^1].Position : fallback;

        var sourcePos = link.Source.GetPosition(link, new[] { first, first });
        var targetPos = link.Target?.GetPosition(link, new[] { last, last });

        var points = new List<Point>();
        if (sourcePos != null) points.Add(sourcePos);
        points.AddRange(link.Vertices.Select(v => v.Position));
        if (targetPos != null) points.Add(targetPos);

        return points;
    }
}
