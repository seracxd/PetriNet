namespace Analysis.Engines;

public enum NetSubclass
{
    Ordinary,           // all arcs are normal arcs with weight 1
    StateMachine,       // each transition has exactly 1 input and 1 output place
    MarkedGraph,        // each place has exactly 1 input and 1 output transition
    FreeChoice,         // shared input place ⟹ transitions share ALL input places
    ExtendedFreeChoice  // shared input place ⟹ each transition has exactly 1 input place
}

/// <summary>
/// Classifies the Petri net into structural subclasses:
/// State Machine, Marked Graph, Free Choice, Extended Free Choice.
/// </summary>
public sealed class ClassificationAnalysis
{
    public bool HasErrors { get; private set; }
    public string? ErrorMsg { get; private set; }

    private readonly HashSet<NetSubclass> _classes = [];

    public bool IsOfType(NetSubclass c) => _classes.Contains(c);
    public IReadOnlySet<NetSubclass> Classes => _classes;

    public void Compute(Analysis.PetriNetSnapshot net)
    {
        HasErrors = false; ErrorMsg = null;
        _classes.Clear();

        if (!net.Places.Any() || !net.Transitions.Any())
        { HasErrors = true; ErrorMsg = "Net has no places or transitions."; return; }

        // ── Ordinary: all arcs are ordinary unit-weight arcs ──────────────
        if (net.Arcs.All(a => a.ArcType == Analysis.PnArcType.Normal && a.Weight == 1))
            _classes.Add(NetSubclass.Ordinary);

        // ── State machine: |pre(t)| = |post(t)| = 1 for every transition ──
        if (net.Transitions.All(t =>
                net.InputArcs(t.Id).Count(a => a.ArcType == Analysis.PnArcType.Normal) == 1 &&
                net.OutputArcs(t.Id).Count(a => a.ArcType == Analysis.PnArcType.Normal) == 1))
            _classes.Add(NetSubclass.StateMachine);

        // ── Marked graph: |pre(p)| = |post(p)| = 1 for every place ─────
        if (net.Places.All(p =>
                net.InputArcsToPlace(p.Id).Count(a => a.ArcType == Analysis.PnArcType.Normal) == 1 &&
                net.OutputArcsFromPlace(p.Id).Count(a => a.ArcType == Analysis.PnArcType.Normal) == 1))
            _classes.Add(NetSubclass.MarkedGraph);

        // ── Free choice: t1 and t2 share input place ⟹ pre(t1) = pre(t2) ──
        bool isFreeChoice = true;
        foreach (var t1 in net.Transitions)
        {
            var pre1 = new HashSet<string>(
                net.InputArcs(t1.Id).Where(a => a.ArcType == Analysis.PnArcType.Normal).Select(a => a.SourceId));
            foreach (var t2 in net.Transitions)
            {
                if (t1.Id == t2.Id) continue;
                var pre2 = new HashSet<string>(
                    net.InputArcs(t2.Id).Where(a => a.ArcType == Analysis.PnArcType.Normal).Select(a => a.SourceId));
                if (pre1.Overlaps(pre2) && !pre1.SetEquals(pre2))
                { isFreeChoice = false; break; }
            }
            if (!isFreeChoice) break;
        }
        if (isFreeChoice) _classes.Add(NetSubclass.FreeChoice);

        // ── Extended free choice: shared input place ⟹ |pre(t)| = 1 ───
        bool isEfc = true;
        foreach (var p in net.Places)
        {
            var consumers = net.OutputArcsFromPlace(p.Id)
                .Where(a => a.ArcType == Analysis.PnArcType.Normal)
                .Select(a => a.TargetId).ToList();
            if (consumers.Count <= 1) continue;
            foreach (var tid in consumers)
            {
                if (net.InputArcs(tid).Count(a => a.ArcType == Analysis.PnArcType.Normal) != 1)
                { isEfc = false; break; }
            }
            if (!isEfc) break;
        }
        if (isEfc) _classes.Add(NetSubclass.ExtendedFreeChoice);
    }

    public string Summary()
    {
        if (HasErrors) return ErrorMsg ?? "Error";
        if (_classes.Count == 0) return "General Petri net";
        return string.Join(", ", _classes.Select(c => c switch
        {
            NetSubclass.Ordinary => "Ordinary",
            NetSubclass.StateMachine => "State Machine",
            NetSubclass.MarkedGraph => "Marked Graph",
            NetSubclass.FreeChoice => "Free Choice",
            NetSubclass.ExtendedFreeChoice => "Extended Free Choice",
            _ => c.ToString()
        }));
    }
}
