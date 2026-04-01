using System.Text;
using System.Xml;
using System.Xml.Linq;
using Core.Models;
using PetriEditor.Shared.Contracts;

namespace PetriEditor.Client.Services;

/// <summary>
/// Serializes and parses PNML 1.3.2 (P/T net grammar).
///
/// Spec: http://www.pnml.org/version-2009/grammar/ptnet
///
/// Extensions used:
///   - <graphics><position x="…" y="…"/></graphics> on each node/arc
///     so round-tripping through PNML preserves diagram layout.
///   - <type value="inhibitorArc"/> / <type value="resetArc"/> on arcs
///     for non-standard arc types.
/// </summary>
public static class PnmlSerializer
{
    private const string PtNetType = "http://www.pnml.org/version-2009/grammar/ptnet";

    // ── Serialization ─────────────────────────────────────────────────────

    public static string Serialize(PetriNetDto net)
    {
        var pnml = new XElement("pnml",
            new XElement("net",
                new XAttribute("id", "net1"),
                new XAttribute("type", PtNetType),
                new XElement("name", new XElement("text", "Petri Net")),
                net.Places.Select(SerializePlace)
                          .Concat(net.Transitions.Select(SerializeTransition))
                          .Concat(net.Arcs.Select((a, i) => SerializeArc(a, i)))
            )
        );

        var settings = new XmlWriterSettings
        {
            Indent            = true,
            IndentChars       = "  ",
            Encoding          = Encoding.UTF8,
            OmitXmlDeclaration = false,
        };

        var sb = new StringBuilder();
        using (var writer = XmlWriter.Create(sb, settings))
            pnml.Save(writer);

        return sb.ToString();
    }

    private static XElement SerializePlace(PlaceDto p) =>
        new("place",
            new XAttribute("id", p.Id),
            new XElement("name",    new XElement("text", p.Name)),
            new XElement("graphics", new XElement("position",
                new XAttribute("x", p.X.ToString("F0", System.Globalization.CultureInfo.InvariantCulture)),
                new XAttribute("y", p.Y.ToString("F0", System.Globalization.CultureInfo.InvariantCulture)))),
            p.Tokens > 0
                ? new XElement("initialMarking", new XElement("text", p.Tokens))
                : null
        );

    private static XElement SerializeTransition(TransitionDto t) =>
        new("transition",
            new XAttribute("id", t.Id),
            new XElement("name",    new XElement("text", t.Name)),
            new XElement("graphics", new XElement("position",
                new XAttribute("x", t.X.ToString("F0", System.Globalization.CultureInfo.InvariantCulture)),
                new XAttribute("y", t.Y.ToString("F0", System.Globalization.CultureInfo.InvariantCulture))))
        );

    private static XElement SerializeArc(ArcDto a, int index)
    {
        var arc = new XElement("arc",
            new XAttribute("id",     $"arc{index}"),
            new XAttribute("source", a.SourceId),
            new XAttribute("target", a.TargetId)
        );

        if (a.Weight > 1)
            arc.Add(new XElement("inscription", new XElement("text", a.Weight)));

        if (a.ArcType != ArcType.Normal)
            arc.Add(new XElement("type",
                new XAttribute("value", a.ArcType == ArcType.Inhibitor ? "inhibitorArc" : "resetArc")));

        // Bend-point positions for round-trip layout fidelity
        if (a.Vertices.Count > 0)
        {
            var positions = a.Vertices.Select(v =>
                new XElement("position",
                    new XAttribute("x", v.X.ToString("F0", System.Globalization.CultureInfo.InvariantCulture)),
                    new XAttribute("y", v.Y.ToString("F0", System.Globalization.CultureInfo.InvariantCulture))));
            arc.Add(new XElement("graphics", positions));
        }

        return arc;
    }

    // ── Parsing ───────────────────────────────────────────────────────────

    public static PetriNetDto Parse(string pnml)
    {
        XDocument doc;
        try { doc = XDocument.Parse(pnml); }
        catch (XmlException ex)
        { throw new FormatException($"Invalid PNML XML: {ex.Message}", ex); }

        var net = doc.Root?.Element("net")
            ?? throw new FormatException("PNML document has no <net> element.");

        var places      = new List<PlaceDto>();
        var transitions = new List<TransitionDto>();
        var arcs        = new List<ArcDto>();

        foreach (var el in net.Elements())
        {
            switch (el.Name.LocalName)
            {
                case "place":
                    places.Add(ParsePlace(el));
                    break;
                case "transition":
                    transitions.Add(ParseTransition(el));
                    break;
                case "arc":
                    arcs.Add(ParseArc(el));
                    break;
            }
        }

        return new PetriNetDto(places, transitions, arcs);
    }

    private static PlaceDto ParsePlace(XElement el)
    {
        var id      = el.Attribute("id")?.Value ?? throw new FormatException("Place missing id attribute.");
        var name    = el.Element("name")?.Element("text")?.Value ?? id;
        var tokens  = int.TryParse(el.Element("initialMarking")?.Element("text")?.Value, out int t) ? t : 0;
        var (x, y)  = ParsePosition(el);
        return new PlaceDto(id, name, tokens, x, y);
    }

    private static TransitionDto ParseTransition(XElement el)
    {
        var id      = el.Attribute("id")?.Value ?? throw new FormatException("Transition missing id attribute.");
        var name    = el.Element("name")?.Element("text")?.Value ?? id;
        var (x, y)  = ParsePosition(el);
        return new TransitionDto(id, name, 0, x, y);
    }

    private static ArcDto ParseArc(XElement el)
    {
        var source = el.Attribute("source")?.Value ?? throw new FormatException("Arc missing source attribute.");
        var target = el.Attribute("target")?.Value ?? throw new FormatException("Arc missing target attribute.");
        var weight = int.TryParse(el.Element("inscription")?.Element("text")?.Value, out int w) ? Math.Max(1, w) : 1;

        var typeValue = el.Element("type")?.Attribute("value")?.Value;
        var arcType   = typeValue switch
        {
            "inhibitorArc" => ArcType.Inhibitor,
            "resetArc"     => ArcType.Reset,
            _              => ArcType.Normal
        };

        var vertices = (el.Element("graphics")?.Elements("position") ?? [])
            .Select(p => new PointDto(
                double.TryParse(p.Attribute("x")?.Value,
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double px) ? px : 0,
                double.TryParse(p.Attribute("y")?.Value,
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double py) ? py : 0))
            .ToList();

        return new ArcDto(source, target, weight, arcType, vertices);
    }

    private static (double x, double y) ParsePosition(XElement el)
    {
        var pos = el.Element("graphics")?.Element("position");
        double x = double.TryParse(pos?.Attribute("x")?.Value,
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out double px) ? px : 0;
        double y = double.TryParse(pos?.Attribute("y")?.Value,
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out double py) ? py : 0;
        return (x, y);
    }
}
