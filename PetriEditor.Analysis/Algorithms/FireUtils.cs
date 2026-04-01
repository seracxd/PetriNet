namespace Analysis.Algorithms;

/// <summary>
/// Shared firing-rule helpers used by the reachability and coverability builders.
/// Mirrors the private logic inside <see cref="Analysis.Engines.StateSpaceAnalysis"/>
/// so the algorithms can be tested and extended independently.
/// </summary>
internal static class FireUtils
{
    /// <summary>True if transition <paramref name="tId"/> is enabled in <paramref name="marking"/>.</summary>
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
    /// Fire transition <paramref name="tId"/> on <paramref name="marking"/> and return the next marking.
    /// Does NOT apply the omega-propagation step — that is the caller's responsibility.
    /// </summary>
    internal static int[] Fire(
        PetriNetSnapshot        net,
        Dictionary<string, int> pIdx,
        int[]                   marking,
        string                  tId)
    {
        var next = (int[])marking.Clone();

        foreach (var arc in net.InputArcs(tId))
        {
            if (!pIdx.TryGetValue(arc.SourceId, out int pi))
                continue;

            if (arc.ArcType == PnArcType.Inhibitor)
                continue;

            if (arc.ArcType == PnArcType.Reset)
            {
                next[pi] = 0;
                continue;
            }

            // Omega stays omega
            if (next[pi] != int.MaxValue)
                next[pi] -= arc.Weight;
        }

        foreach (var arc in net.OutputArcs(tId))
        {
            if (pIdx.TryGetValue(arc.TargetId, out int pi))
            {
                // Omega stays omega
                if (next[pi] != int.MaxValue)
                    next[pi] += arc.Weight;
            }
        }

        return next;
    }
}
