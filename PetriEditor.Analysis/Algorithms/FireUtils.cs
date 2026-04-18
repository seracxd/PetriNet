namespace Analysis.Algorithms;

/// <summary>
/// Shared firing-rule helpers used by the reachability and coverability builders.
/// Mirrors the private logic inside <see cref="Analysis.Engines.StateSpaceAnalysis"/>
/// so the algorithms can be tested and extended independently.
/// </summary>
internal static class FireUtils
{
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
                // Omega (int.MaxValue) counts as > 0, so inhibitor blocks
                if (marking[pi] != 0)
                    return false;
            }
            else
            {
                // Omega is always >= any weight, so treat it as enabled
                if (marking[pi] != int.MaxValue && marking[pi] < arc.Weight)
                    return false;
            }
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
            if (next[pi] != int.MaxValue) next[pi] -= arc.Weight;
        }

        // Pass 2 — resets (always clear to 0, regardless of prior subtractions)
        foreach (var arc in net.InputArcs(tId))
        {
            if (arc.ArcType != PnArcType.Reset) continue;
            if (!pIdx.TryGetValue(arc.SourceId, out int pi)) continue;
            next[pi] = 0;
        }

        // Pass 3 — productions
        foreach (var arc in net.OutputArcs(tId))
        {
            if (pIdx.TryGetValue(arc.TargetId, out int pi))
            {
                if (next[pi] != int.MaxValue) next[pi] += arc.Weight;
            }
        }

        return next;
    }
}
