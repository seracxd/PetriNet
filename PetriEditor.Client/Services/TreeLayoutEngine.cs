namespace PetriNetAnalyzer.Services;

/// <summary>
/// Reingold-Tilford-style compact grid layout for tree views.
/// Every node gets an integer column; rows map directly to depth levels.
/// Used by both <c>CoverabilityTreeViewSvg</c> and any future tree-view component.
/// </summary>
public static class TreeLayoutEngine
{
    /// <summary>
    /// Assigns an integer <c>Col</c> to each node id in <paramref name="layout"/>.
    /// </summary>
    public static void AssignColumns(
        int                          root,
        Dictionary<int, List<int>>   children,
        Dictionary<int, int>         depth,
        Dictionary<int, int>         colOut)
    {
        var relCol = new Dictionary<int, int>();
        ComputeRelative(root, children, depth, relCol);

        int minRel = relCol.Values.Min();
        int shift  = -minRel;

        // Guard: shift right past any already-occupied column on the same row
        // (relevant only in multi-root scenarios; single-root trees are always clean)
        var rowOccupied = new Dictionary<int, HashSet<int>>();
        foreach (var (nid, rc) in relCol)
        {
            int row    = depth[nid];
            int absCol = rc + shift;
            if (rowOccupied.TryGetValue(row, out var occ) && occ.Contains(absCol))
                shift = occ.Max() + 1 - rc;
        }

        foreach (var (nid, rc) in relCol)
        {
            int row    = depth[nid];
            int absCol = rc + shift;
            colOut[nid] = absCol;
            if (!rowOccupied.ContainsKey(row)) rowOccupied[row] = new();
            rowOccupied[row].Add(absCol);
        }
    }

    // ── Iterative post-order Reingold-Tilford layout ──────────────────────

    private static void ComputeRelative(
        int root, Dictionary<int, List<int>> children,
        Dictionary<int, int> depth, Dictionary<int, int> relCol)
    {
        // Build post-order sequence iteratively (avoids stack overflow on deep trees)
        var postOrder = new List<int>();
        var stack = new Stack<int>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            int cur = stack.Pop();
            postOrder.Add(cur);
            foreach (int ch in children[cur])
                stack.Push(ch);
        }
        postOrder.Reverse(); // leaves first, root last

        var leftContour  = new Dictionary<int, Dictionary<int, int>>();
        var rightContour = new Dictionary<int, Dictionary<int, int>>();

        foreach (int id in postOrder)
        {
            int myDepth = depth[id];
            var ch = children[id];

            if (ch.Count == 0)
            {
                relCol[id]       = 0;
                leftContour[id]  = new() { [myDepth] = 0 };
                rightContour[id] = new() { [myDepth] = 0 };
                continue;
            }

            var childRoots = new List<(int ChildId, int Offset)>();
            var mergedLeft  = new Dictionary<int, int>(leftContour[ch[0]]);
            var mergedRight = new Dictionary<int, int>(rightContour[ch[0]]);
            childRoots.Add((ch[0], 0));
            int nextOffset = 1;

            for (int i = 1; i < ch.Count; i++)
            {
                var cl = leftContour[ch[i]];
                var cr = rightContour[ch[i]];

                // Minimum shift so this subtree clears the merged right contour
                int minShift = nextOffset;
                foreach (var (d, leftVal) in cl)
                    if (mergedRight.TryGetValue(d, out int rightVal))
                    {
                        int needed = rightVal - leftVal + 2; // 1-column gap
                        if (needed > minShift) minShift = needed;
                    }

                childRoots.Add((ch[i], minShift));

                foreach (var (d, v) in cl)
                {
                    int sv = v + minShift;
                    if (!mergedLeft.ContainsKey(d) || sv < mergedLeft[d]) mergedLeft[d] = sv;
                }
                foreach (var (d, v) in cr)
                {
                    int sv = v + minShift;
                    if (!mergedRight.ContainsKey(d) || sv > mergedRight[d]) mergedRight[d] = sv;
                }

                nextOffset = childRoots[^1].Offset + 1;
            }

            // Center parent over its children
            int leftChild  = childRoots[0].Offset  + relCol[ch[0]];
            int rightChild = childRoots[^1].Offset + relCol[ch[^1]];
            int parentCol  = (leftChild + rightChild) / 2;

            relCol[id] = 0;
            foreach (var (childId, offset) in childRoots)
                ShiftSubtree(childId, children, relCol, offset - parentCol);

            // Rebuild contours relative to parent = 0
            int parentShift = -parentCol;
            var finalLeft  = new Dictionary<int, int> { [myDepth] = 0 };
            var finalRight = new Dictionary<int, int> { [myDepth] = 0 };
            foreach (var (d, v) in mergedLeft)
            {
                int sv = v + parentShift;
                if (!finalLeft.ContainsKey(d)  || sv < finalLeft[d])  finalLeft[d]  = sv;
            }
            foreach (var (d, v) in mergedRight)
            {
                int sv = v + parentShift;
                if (!finalRight.ContainsKey(d) || sv > finalRight[d]) finalRight[d] = sv;
            }

            leftContour[id]  = finalLeft;
            rightContour[id] = finalRight;
        }
    }

    private static void ShiftSubtree(int id, Dictionary<int, List<int>> children,
                                     Dictionary<int, int> relCol, int delta)
    {
        var stack = new Stack<int>();
        stack.Push(id);
        while (stack.Count > 0)
        {
            int cur = stack.Pop();
            relCol[cur] += delta;
            foreach (int ch in children[cur])
                stack.Push(ch);
        }
    }
}
