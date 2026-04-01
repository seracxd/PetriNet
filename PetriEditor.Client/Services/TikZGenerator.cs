using System.Text;
using Core.Models;
using PetriEditor.Shared.Contracts;

namespace PetriEditor.Client.Services;

/// <summary>
/// Generates a standalone LaTeX/TikZ document from a <see cref="PetriNetDto"/>.
///
/// Uses the standard TikZ petri library:
///   \usetikzlibrary{petri,positioning,arrows}
///
/// Pixel coordinates from the diagram are divided by 50 to get centimetres,
/// and Y is negated because SVG Y-axis grows downward while TikZ grows upward.
/// </summary>
public static class TikZGenerator
{
    private const double Scale = 50.0;   // pixels per centimetre

    public static string Generate(PetriNetDto net)
    {
        var sb = new StringBuilder();

        sb.AppendLine(@"\documentclass{standalone}");
        sb.AppendLine(@"\usepackage{tikz}");
        sb.AppendLine(@"\usetikzlibrary{petri,positioning,arrows.meta}");
        sb.AppendLine();
        sb.AppendLine(@"\begin{document}");
        sb.AppendLine(@"\begin{tikzpicture}[>=Stealth,bend angle=45,auto,");
        sb.AppendLine(@"  every place/.style={draw,circle,minimum size=1.4cm,inner sep=0pt},");
        sb.AppendLine(@"  every transition/.style={draw,rectangle,minimum width=0.4cm,minimum height=1.0cm,fill=black},");
        sb.AppendLine(@"  inhibitor/.style={-o,thick},");
        sb.AppendLine(@"  reset/.style={-},");
        sb.AppendLine(@"  post/.style={->,thick}]");
        sb.AppendLine();

        // ── Places ────────────────────────────────────────────────────────
        sb.AppendLine("  % Places");
        foreach (var p in net.Places)
        {
            var (x, y) = ToTikZ(p.X, p.Y);
            var tokens = p.Tokens > 0 ? $",tokens={p.Tokens}" : "";
            sb.AppendLine($@"  \node[place{tokens}] ({TikZId(p.Id)}) at ({x},{y}) {{{EscapeTikZ(p.Name)}}};");
        }

        sb.AppendLine();

        // ── Transitions ───────────────────────────────────────────────────
        sb.AppendLine("  % Transitions");
        foreach (var t in net.Transitions)
        {
            var (x, y) = ToTikZ(t.X, t.Y);
            sb.AppendLine($@"  \node[transition] ({TikZId(t.Id)}) at ({x},{y}) {{{EscapeTikZ(t.Name)}}};");
        }

        sb.AppendLine();

        // ── Arcs ──────────────────────────────────────────────────────────
        sb.AppendLine("  % Arcs");
        foreach (var a in net.Arcs)
        {
            var src = TikZId(a.SourceId);
            var tgt = TikZId(a.TargetId);

            string style = a.ArcType switch
            {
                ArcType.Inhibitor => "inhibitor",
                ArcType.Reset     => "reset",
                _                 => "post"
            };

            string label = (a.ArcType == ArcType.Normal && a.Weight > 1)
                ? $",node midway,above,font=\\small {{{a.Weight}}}"
                : "";

            if (a.Vertices.Count > 0)
            {
                // Bend through vertices using explicit coordinates
                var coords = string.Join(" -- ", a.Vertices.Select(v =>
                {
                    var (vx, vy) = ToTikZ(v.X, v.Y);
                    return $"({vx},{vy})";
                }));
                sb.AppendLine($@"  \draw[{style}] ({src}) -- {coords} -- ({tgt});");
            }
            else if (!string.IsNullOrEmpty(label))
            {
                sb.AppendLine($@"  \draw[{style}] ({src}) to node{label} ({tgt});");
            }
            else
            {
                sb.AppendLine($@"  \draw[{style}] ({src}) to ({tgt});");
            }
        }

        sb.AppendLine();
        sb.AppendLine(@"\end{tikzpicture}");
        sb.AppendLine(@"\end{document}");

        return sb.ToString();
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static (string x, string y) ToTikZ(double px, double py)
    {
        double x =  px / Scale;
        double y = -py / Scale;   // flip Y: SVG down = TikZ up
        return (x.ToString("F2", System.Globalization.CultureInfo.InvariantCulture),
                y.ToString("F2", System.Globalization.CultureInfo.InvariantCulture));
    }

    /// Sanitise an arbitrary ID string into a valid TikZ node name.
    private static string TikZId(string id) =>
        id.Replace("-", "").Replace(" ", "_").Replace(".", "_");

    /// Escape special LaTeX characters in a display name.
    private static string EscapeTikZ(string name) =>
        name.Replace("\\", "\\textbackslash{}")
            .Replace("&",  "\\&")
            .Replace("%",  "\\%")
            .Replace("$",  "\\$")
            .Replace("#",  "\\#")
            .Replace("_",  "\\_")
            .Replace("{",  "\\{")
            .Replace("}",  "\\}")
            .Replace("~",  "\\textasciitilde{}")
            .Replace("^",  "\\textasciicircum{}");
}
