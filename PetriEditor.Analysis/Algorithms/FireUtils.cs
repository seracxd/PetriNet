namespace Analysis.Algorithms;

/// <summary>
/// Shared firing-rule helpers used by the reachability and coverability builders.
/// Mirrors the private logic inside <see cref="Analysis.Engines.StateSpaceAnalysis"/>
/// so the algorithms can be tested and extended independently.
/// </summary>
internal static class FireUtils
{
    /// <summary>
    /// Saturation ceiling for finite token counts. Below int.MaxValue (which is ω) by
    /// a wide margin so that an addition near the cap cannot overflow into ω or wrap
    /// to negative. Counts that reach this value are clamped here and stay here —
    /// the marking is effectively "very large" and any downstream coverability check
    /// will treat the place as unbounded.
    /// </summary>
    internal const int MaxFiniteTokens = int.MaxValue / 2;

    /// <summary>True if transition <paramref name="tId"/> is enabled in <paramref name="marking"/> (ignores priority).</summary>
    internal static bool IsEnabled(
        PetriNetSnapshot        net,
        Dictionary<string, int> pIdx,
        int[]                   marking,
        string                  tId)
    {
        foreach (var arc in net.InputArcs(tId))
        {
            if (!pIdx.TryGetValue(arc.SourceId, out int pi))
                continue;

            if (arc.ArcType == PnArcType.Inhibitor)
            {
                // Weighted inhibitor: blocks when tokens >= weight.
                // Omega (int.MaxValue) is >= any finite weight, so it always blocks.
                if (marking[pi] == int.MaxValue || marking[pi] >= arc.Weight)
                    return false;
            }
            else if (arc.ArcType == PnArcType.Normal)
            {
                // Omega is always >= any weight, so treat it as enabled
                if (marking[pi] != int.MaxValue && marking[pi] < arc.Weight)
                    return false;
            }
            // Reset arcs do not guard enablement (Aalst, Def. 2)
        }
        return true;
    }

    /// <summary>
    /// Returns the subset of transitions that are actually fireable under strict priority semantics:
    /// only transitions in the highest-priority tier among all enabled transitions.
    /// If all transitions have priority 0, returns all enabled transitions unchanged.
    /// </summary>
    internal static IEnumerable<PnTransition> GetFireableTransitions(
        PetriNetSnapshot        net,
        Dictionary<string, int> pIdx,
        int[]                   marking)
    {
        var enabled = net.Transitions.Where(t => IsEnabled(net, pIdx, marking, t.Id)).ToList();
        if (enabled.Count == 0) return enabled;

        int maxPriority = enabled.Max(t => t.Priority);
        // If no transition has non-zero priority, no filtering needed
        if (maxPriority == 0) return enabled;

        return enabled.Where(t => t.Priority == maxPriority);
    }

    /// <summary>
    /// Fire transition <paramref name="tId"/> on <paramref name="marking"/> and return the next marking.
    /// Does NOT apply the omega-propagation step — that is the caller's responsibility.
    /// Processing order: normal consumptions → resets → productions, so reset always
    /// wins on a place and the result is independent of arc iteration order.
    /// </summary>
    internal static int[] Fire(
        PetriNetSnapshot        net,
        Dictionary<string, int> pIdx,
        int[]                   marking,
        string                  tId)
    {
        var next = (int[])marking.Clone();

        // Pass 1 — normal consumptions (skip inhibitor, skip reset)
        foreach (var arc in net.InputArcs(tId))
        {
            if (arc.ArcType != PnArcType.Normal) continue;
            if (!pIdx.TryGetValue(arc.SourceId, out int pi)) continue;
            if (next[pi] == int.MaxValue) continue;        // ω − k = ω
            next[pi] -= arc.Weight;
            if (next[pi] < 0) next[pi] = 0;                 // defensive: IsEnabled should prevent this
        }

        // Pass 2 — resets (always clear to 0, regardless of prior subtractions)
        foreach (var arc in net.InputArcs(tId))
        {
            if (arc.ArcType != PnArcType.Reset) continue;
            if (!pIdx.TryGetValue(arc.SourceId, out int pi)) continue;
            next[pi] = 0;
        }

        // Pass 3 — productions (saturating add: never overflow into ω or wrap)
        foreach (var arc in net.OutputArcs(tId))
        {
            if (pIdx.TryGetValue(arc.TargetId, out int pi))
            {
                if (next[pi] == int.MaxValue) continue;     // ω + k = ω
                long sum = (long)next[pi] + arc.Weight;
                next[pi] = sum >= MaxFiniteTokens ? MaxFiniteTokens : (int)sum;
            }
        }

        return next;
    }
}
