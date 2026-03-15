using Core.Models;

namespace Analysis.Simulation;

/// <summary>
/// Pure Petri net simulation logic — no UI, no diagram dependency.
/// Caller snapshots the net into plain Core.Models types and passes them to Init().
/// </summary>
public class PetriNetSimulator
{
    // ── Net snapshot (immutable after Init) ───────────────────────────────

    public record PlaceInfo(string Id, string Name, int InitialTokens);
    public record TransitionInfo(string Id, string Name, int Priority);
    public record ArcInfo(string PlaceId, string TransitionId, bool PlaceIsSource, int Weight, ArcType Type);

    public IReadOnlyList<PlaceInfo> Places { get; private set; } = [];
    public IReadOnlyList<TransitionInfo> Transitions { get; private set; } = [];
    public IReadOnlyList<ArcInfo> Arcs { get; private set; } = [];

    // ── Simulation state ──────────────────────────────────────────────────

    /// <summary>Current marking: PlaceId → token count.</summary>
    public Dictionary<string, int> Marking { get; private set; } = new();

    private Dictionary<string, int> _initialMarking = new();

    /// <summary>Ordered history of fired transition IDs (oldest first).</summary>
    public List<string> FiringHistory { get; } = new();

    public bool IsInitialised { get; private set; }

    // ── Init ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Initialise the simulator from plain net data.
    /// The caller is responsible for translating diagram models into these types.
    /// </summary>
    public void Init(
        IEnumerable<PlaceInfo> places,
        IEnumerable<TransitionInfo> transitions,
        IEnumerable<ArcInfo> arcs)
    {
        Places = places.ToList();
        Transitions = transitions.ToList();
        Arcs = arcs.ToList();

        _initialMarking = Places.ToDictionary(p => p.Id, p => p.InitialTokens);
        Marking = new Dictionary<string, int>(_initialMarking);
        FiringHistory.Clear();
        IsInitialised = true;
    }

    // ── Query ─────────────────────────────────────────────────────────────

    public HashSet<string> GetEnabledTransitions() =>
        Transitions.Where(t => IsEnabled(t.Id)).Select(t => t.Id).ToHashSet();

    public bool IsEnabled(string transitionId)
    {
        foreach (var arc in Arcs.Where(a => a.TransitionId == transitionId && a.PlaceIsSource))
        {
            int tokens = Marking.GetValueOrDefault(arc.PlaceId, 0);
            switch (arc.Type)
            {
                case ArcType.Normal:
                    if (tokens < arc.Weight) return false;
                    break;
                case ArcType.Inhibitor:
                    if (tokens >= arc.Weight) return false;
                    break;
                case ArcType.Reset:
                    break; // reset arcs don't guard enabledness
            }
        }
        return true;
    }

    // ── Fire ──────────────────────────────────────────────────────────────

    /// <summary>Fire a transition. Returns false if not enabled.</summary>
    public bool Fire(string transitionId)
    {
        if (!IsEnabled(transitionId)) return false;

        foreach (var arc in Arcs.Where(a => a.TransitionId == transitionId))
        {
            int current = Marking.GetValueOrDefault(arc.PlaceId, 0);
            if (arc.PlaceIsSource)
            {
                Marking[arc.PlaceId] = arc.Type switch
                {
                    ArcType.Normal => current - arc.Weight,
                    ArcType.Reset => 0,
                    ArcType.Inhibitor => current, // consumes nothing
                    _ => current
                };
            }
            else
            {
                // Output arc — produce tokens
                Marking[arc.PlaceId] = current + arc.Weight;
            }
        }

        FiringHistory.Add(transitionId);
        return true;
    }

    // ── Reset / Stop ──────────────────────────────────────────────────────

    public void Reset()
    {
        Marking = new Dictionary<string, int>(_initialMarking);
        FiringHistory.Clear();
    }

    public void Stop()
    {
        Reset();
        IsInitialised = false;
        Places = [];
        Transitions = [];
        Arcs = [];
    }

    /// <summary>Truncates history to stepIndex+1 and replays marking from scratch.</summary>
    public void RewindToStep(int stepIndex)
    {
        // Clamp
        stepIndex = Math.Clamp(stepIndex, -1, FiringHistory.Count - 1);

        // Truncate history
        if (stepIndex < 0)
            FiringHistory.Clear();
        else
            while (FiringHistory.Count > stepIndex + 1)
                FiringHistory.RemoveAt(FiringHistory.Count - 1);

        // Replay marking from initial
        Marking = new Dictionary<string, int>(_initialMarking);
        foreach (var id in FiringHistory)
            ReplayFire(id);
    }

    private void ReplayFire(string transitionId)
    {
        foreach (var arc in Arcs.Where(a => a.TransitionId == transitionId))
        {
            int current = Marking.GetValueOrDefault(arc.PlaceId, 0);
            if (arc.PlaceIsSource)
            {
                Marking[arc.PlaceId] = arc.Type switch
                {
                    ArcType.Normal => current - arc.Weight,
                    ArcType.Reset => 0,
                    ArcType.Inhibitor => current,
                    _ => current
                };
            }
            else
            {
                Marking[arc.PlaceId] = current + arc.Weight;
            }
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    public string GetPlaceName(string id) => Places.FirstOrDefault(p => p.Id == id)?.Name ?? id;
    public string GetTransitionName(string id) => Transitions.FirstOrDefault(t => t.Id == id)?.Name ?? id;
}