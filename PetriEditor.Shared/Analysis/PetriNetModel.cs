namespace Analysis;

/// <summary>
/// Lightweight, immutable snapshot of the Petri net used for all analysis.
/// Decoupled from Blazor diagram models so analysis can run on a background thread.
/// </summary>
public sealed class PetriNetSnapshot
{
    public IReadOnlyList<PnPlace>      Places      { get; }
    public IReadOnlyList<PnTransition> Transitions { get; }
    public IReadOnlyList<PnArc>        Arcs        { get; }

    public IReadOnlyDictionary<string, PnPlace>      PlaceById      { get; }
    public IReadOnlyDictionary<string, PnTransition> TransitionById { get; }

    public PetriNetSnapshot(
        IEnumerable<PnPlace>      places,
        IEnumerable<PnTransition> transitions,
        IEnumerable<PnArc>        arcs)
    {
        Places      = places.ToList();
        Transitions = transitions.ToList();
        Arcs        = arcs.ToList();
        PlaceById      = Places.ToDictionary(p => p.Id);
        TransitionById = Transitions.ToDictionary(t => t.Id);
    }

    // ── Structural helpers ────────────────────────────────────────────────

    /// Arcs going INTO a transition (pre-set arcs from places)
    public IEnumerable<PnArc> InputArcs(string transitionId) =>
        Arcs.Where(a => a.TargetId == transitionId);

    /// Arcs going OUT of a transition (post-set arcs to places)
    public IEnumerable<PnArc> OutputArcs(string transitionId) =>
        Arcs.Where(a => a.SourceId == transitionId);

    /// Arcs going INTO a place (from transitions)
    public IEnumerable<PnArc> InputArcsToPlace(string placeId) =>
        Arcs.Where(a => a.TargetId == placeId);

    /// Arcs going OUT of a place (to transitions)
    public IEnumerable<PnArc> OutputArcsFromPlace(string placeId) =>
        Arcs.Where(a => a.SourceId == placeId);

    /// All transitions connected to a place in either direction
    public IEnumerable<PnTransition> ConnectedTransitions(string placeId) =>
        Arcs.Where(a => a.SourceId == placeId || a.TargetId == placeId)
            .Select(a => a.SourceId == placeId
                ? TransitionById.GetValueOrDefault(a.TargetId)
                : TransitionById.GetValueOrDefault(a.SourceId))
            .Where(t => t != null)
            .Select(t => t!)
            .Distinct();

    /// Incidence matrix W[p,t] = production - consumption weight
    public int[,] IncidenceMatrix()
    {
        int p = Places.Count, t = Transitions.Count;
        var W    = new int[p, t];
        var pIdx = Places     .Select((pl, i) => (pl.Id, i)).ToDictionary(x => x.Id, x => x.i);
        var tIdx = Transitions.Select((tr, i) => (tr.Id, i)).ToDictionary(x => x.Id, x => x.i);

        foreach (var arc in Arcs)
        {
            if (arc.ArcType != PnArcType.Normal) continue;

            if (pIdx.TryGetValue(arc.SourceId, out int pi) && tIdx.TryGetValue(arc.TargetId, out int ti))
                W[pi, ti] -= arc.Weight;   // place → transition: consumption
            else if (tIdx.TryGetValue(arc.SourceId, out ti) && pIdx.TryGetValue(arc.TargetId, out pi))
                W[pi, ti] += arc.Weight;   // transition → place: production
        }
        return W;
    }
}

public sealed class PnPlace(string id, string name, int tokens)
{
    public string Id     { get; } = id;
    public string Name   { get; } = name;
    public int    Tokens { get; } = tokens;
}

public sealed class PnTransition(string id, string name, int priority = 0)
{
    public string Id       { get; } = id;
    public string Name     { get; } = name;
    public int    Priority { get; } = priority;
}

public sealed class PnArc(string sourceId, string targetId, int weight, PnArcType arcType)
{
    public string    SourceId { get; } = sourceId;
    public string    TargetId { get; } = targetId;
    public int       Weight   { get; } = weight;
    public PnArcType ArcType  { get; } = arcType;
}

public enum PnArcType { Normal, Inhibitor, Reset }
