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

        var mid = GetMidpoint(link);
        if (mid == null) return null;

        return new Point(mid.X + link.WeightLabelOffset.X,
                         mid.Y + link.WeightLabelOffset.Y);
    }

    /// <summary>Midpoint of the middle segment of the arc.</summary>
    public static Point? GetMidpoint(PetriLinkModel link)
    {
        var pts = GetFullLinkPoints(link);
        if (pts.Count < 2) return null;
        var seg = (pts.Count - 2) / 2;
        var a = pts[seg]; var b = pts[seg + 1];
        return new Point((a.X + b.X) / 2, (a.Y + b.Y) / 2);
    }

    public static List<Point> GetFullLinkPoints(LinkModel link)
    {
        var verts = link.Vertices.Select(v => v.Position).ToList();

        var srcHint = verts.Count > 0 ? verts[0]
                    : link.Target?.GetPlainPosition() ?? new Point(0, 0);
        var tgtHint = verts.Count > 0 ? verts[^1]
                    : link.Source?.GetPlainPosition() ?? new Point(0, 0);

        var sourcePos = link.Source.GetPosition(link, new[] { srcHint, srcHint });
        var targetPos = link.Target?.GetPosition(link, new[] { tgtHint, tgtHint });

        var pts = new List<Point>();
        if (sourcePos != null) pts.Add(sourcePos);
        pts.AddRange(verts);
        if (targetPos != null) pts.Add(targetPos);
        return pts;
    }
}