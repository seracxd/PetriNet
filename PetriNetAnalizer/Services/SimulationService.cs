using Analysis;
using Analysis.Simulation;
using Blazor.Diagrams.Core.Anchors;
using Blazor.Diagrams.Core.Models;
using Core.Models;
using PetriNetAnalyzer.DiagramModels;

namespace PetriNetAnalyzer.Services;

/// <summary>
/// Bridges PetriNetSimulator to the live diagram.
/// Writes token counts back onto Place.Tokens and refreshes nodes
/// so PlaceComponent re-renders automatically.
/// </summary>
public class SimulationService : IDisposable
{
    private readonly PetriNetManager _manager;
    private readonly DiagramStateService _state;
    private readonly PetriNetSimulator _sim = new();
    private Dictionary<string, int> _initialTokenSnapshot = new();

    private System.Threading.Timer? _autoTimer;

    // Time-travel: when viewing a past step we keep the full history here
    // so the user can always "return to present"
    private List<string>? _savedFuture = null;
    public bool IsViewingHistory => _savedFuture != null;
    public int LiveStepCount => IsViewingHistory ? _savedFuture!.Count : _sim.FiringHistory.Count;
    /// <summary>Full history including future steps when time-travelling.</summary>
    public IReadOnlyList<string> FullHistory => (IReadOnlyList<string>?)_savedFuture ?? _sim.FiringHistory;

    public bool IsActive => _sim.IsInitialised;
    public bool IsAutoFiring { get; private set; }
    public int FiringDelay { get; set; } = 1000; // ms

    public event Action? OnChanged;

    // Firing mode — HighestPriority is the default for auto/step; Manual means user picks in panel
    public enum FireMode { HighestPriority, Random }
    public FireMode Mode { get; set; } = FireMode.HighestPriority;

    public IReadOnlyList<PetriNetSimulator.TransitionInfo> Transitions => _sim.Transitions;
    public IReadOnlyList<PetriNetSimulator.PlaceInfo> Places => _sim.Places;
    public IReadOnlyList<string> History => _sim.FiringHistory;
    public HashSet<string> EnabledTransitions => _sim.GetEnabledTransitions();
    public Dictionary<string, int> Marking => _sim.Marking;
    public int StepCount => _sim.FiringHistory.Count;
    public bool IsDeadlock => IsActive && !EnabledTransitions.Any();

    // Firing stats per transition — for the right panel
    public Dictionary<string, int> FiringCounts { get; } = new();

    // Last fired transition id — for visual highlight
    public string? LastFiredId { get; private set; }

    public SimulationService(PetriNetManager manager, DiagramStateService state)
    {
        _manager = manager;
        _state = state;
    }

    // ── Start / Stop ──────────────────────────────────────────────────────

    public void Start()
    {
        var places = _manager.Diagram.Nodes.OfType<PlaceNode>().ToList();
        var transitions = _manager.Diagram.Nodes.OfType<TransitionNode>().ToList();
        var links = _manager.Diagram.Links.OfType<PetriLinkModel>()
                            .Where(l => !l.IsDraggingEndpoint && l.Target != null).ToList();

        var placeInfos = places.Select(p =>
            new PetriNetSimulator.PlaceInfo(p.Data.Id, p.Data.Name, p.Data.Tokens)).ToList();
        var transInfos = transitions.Select(t =>
            new PetriNetSimulator.TransitionInfo(t.Data.Id, t.Data.Name, t.Data.Priority)).ToList();

        // Snapshot initial tokens so we can restore them when simulation stops
        _initialTokenSnapshot = places.ToDictionary(p => p.Data.Id, p => p.Data.Tokens);

        var placeIds = placeInfos.Select(x => x.Id).ToHashSet();
        var transIds = transInfos.Select(x => x.Id).ToHashSet();

        NodeModel? GetParent(Anchor a) => a.Model switch
        {
            NodeModel n => n,
            PortModel pm => pm.Parent,
            _ => null
        };

        var arcInfos = new List<PetriNetSimulator.ArcInfo>();
        foreach (var link in links)
        {
            var src = GetParent(link.Source);
            var tgt = GetParent(link.Target!);
            if (src == null || tgt == null) continue;

            string? srcId = src is PlaceNode ps ? ps.Data.Id : src is TransitionNode ts ? ts.Data.Id : null;
            string? tgtId = tgt is PlaceNode pt ? pt.Data.Id : tgt is TransitionNode tt ? tt.Data.Id : null;
            if (srcId == null || tgtId == null) continue;

            bool placeIsSource = src is PlaceNode;
            string placeId = placeIsSource ? srcId : tgtId;
            string transitionId = placeIsSource ? tgtId : srcId;
            if (!placeIds.Contains(placeId) || !transIds.Contains(transitionId)) continue;

            arcInfos.Add(new PetriNetSimulator.ArcInfo(placeId, transitionId, placeIsSource, link.Weight, link.ArcType));
        }

        _sim.Init(placeInfos, transInfos, arcInfos);
        _state.IsSimulating = true;
        _manager.IsSimulating = true;
        LastFiredId = null;
        SetNodeLock(true);
        PushMarkingToDiagram();
        OnChanged?.Invoke();
    }

    public void Stop()
    {
        StopAuto();
        _sim.Stop();
        _state.IsSimulating = false;
        _manager.IsSimulating = false;
        _savedFuture = null;
        FiringCounts.Clear();
        LastFiredId = null;
        SetNodeLock(false);
        PushMarkingToDiagram(); // restores initial tokens
        OnChanged?.Invoke();
    }

    public void Reset()
    {
        StopAuto();
        _sim.Reset();
        _savedFuture = null;
        FiringCounts.Clear();
        LastFiredId = null;
        PushMarkingToDiagram();
        OnChanged?.Invoke();
    }

    // ── Manual step ───────────────────────────────────────────────────────

    public bool StepManual(string transitionId)
    {
        if (!IsActive) return false;
        // Continuing from a past step discards the saved future
        _savedFuture = null;
        LastFiredId = transitionId;
        bool ok = _sim.Fire(transitionId);
        if (ok)
        {
            FiringCounts.TryGetValue(transitionId, out var n);
            FiringCounts[transitionId] = n + 1;
            PushMarkingToDiagram();
        }
        OnChanged?.Invoke();
        return ok;
    }

    // Fires the highest-priority enabled transition (random tiebreak)
    public bool StepAuto()
    {
        if (!IsActive || IsDeadlock) return false;

        var enabled = EnabledTransitions;
        var candidates = _sim.Transitions
            .Where(t => enabled.Contains(t.Id))
            .ToList();

        if (!candidates.Any()) return false;

        string? chosen = Mode switch
        {
            FireMode.HighestPriority =>
                candidates
                    .GroupBy(t => t.Priority)
                    .OrderByDescending(g => g.Key)
                    .First()
                    .OrderBy(_ => Guid.NewGuid())
                    .First().Id,
            FireMode.Random =>
                candidates[new Random().Next(candidates.Count)].Id,
            _ => null
        };

        if (chosen == null) return false;
        return StepManual(chosen);
    }

    // ── Auto firing ───────────────────────────────────────────────────────

    public void StartAuto()
    {
        if (IsAutoFiring) return;
        IsAutoFiring = true;
        _autoTimer = new System.Threading.Timer(_ =>
        {
            if (!IsActive || IsDeadlock)
            {
                StopAuto();
                return;
            }
            StepAuto();
        }, null, FiringDelay, FiringDelay);
        OnChanged?.Invoke();
    }

    public void StopAuto()
    {
        if (!IsAutoFiring) return;
        IsAutoFiring = false;
        _autoTimer?.Dispose();
        _autoTimer = null;
        OnChanged?.Invoke();
    }

    public void ToggleAuto()
    {
        if (IsAutoFiring) StopAuto(); else StartAuto();
    }

    // ── Node lock ─────────────────────────────────────────────────────────

    private void SetNodeLock(bool locked)
    {
        foreach (var node in _manager.Diagram.Nodes)
            node.Locked = locked;
    }

    // ── History time-travel ───────────────────────────────────────────────

    /// <summary>
    /// View history at a given step index. Saves the full history so ReturnToPresent() works.
    /// </summary>
    public void JumpToStep(int stepIndex)
    {
        StopAuto();
        // Save full history before truncating (only if not already viewing history)
        if (_savedFuture == null)
            _savedFuture = new List<string>(_sim.FiringHistory);
        _sim.RewindToStep(stepIndex);
        LastFiredId = stepIndex >= 0 ? _sim.FiringHistory.LastOrDefault() : null;
        PushMarkingToDiagram();
        OnChanged?.Invoke();
    }

    /// <summary>Restore to the live "present" step (most recent fired state).</summary>
    public void ReturnToPresent()
    {
        if (_savedFuture == null) return;
        var full = _savedFuture;
        _savedFuture = null;
        _sim.RewindToStep(full.Count - 1);
        // The rewind may have cleared future steps beyond current; replay the full list
        while (_sim.FiringHistory.Count < full.Count)
        {
            var id = full[_sim.FiringHistory.Count];
            _sim.Fire(id);
        }
        LastFiredId = _sim.FiringHistory.LastOrDefault();
        PushMarkingToDiagram();
        OnChanged?.Invoke();
    }

    // ── Diagram sync ──────────────────────────────────────────────────────

    /// <summary>
    /// Writes simulation marking back onto Place.Tokens and refreshes nodes.
    /// When simulation is stopped, restores the initial token snapshot.
    /// </summary>
    private void PushMarkingToDiagram()
    {
        foreach (var placeNode in _manager.Diagram.Nodes.OfType<PlaceNode>())
        {
            if (_sim.IsInitialised && _sim.Marking.TryGetValue(placeNode.Data.Id, out var tokens))
                placeNode.Data.Tokens = tokens;
            else if (_initialTokenSnapshot.TryGetValue(placeNode.Data.Id, out var initial))
                placeNode.Data.Tokens = initial;
            placeNode.Refresh();
        }

        foreach (var transNode in _manager.Diagram.Nodes.OfType<TransitionNode>())
            transNode.Refresh();
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    public string GetTransitionName(string id) => _sim.GetTransitionName(id);

    public void Dispose()
    {
        _autoTimer?.Dispose();
    }
}