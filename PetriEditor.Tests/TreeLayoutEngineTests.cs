using PetriNetAnalyzer.Services;

namespace PetriEditor.Tests;

public class TreeLayoutEngineTests
{
    // ── Helpers ───────────────────────────────────────────────────────────

    /// Build the depth and children dicts for a tree described as parent→children pairs.
    private static (Dictionary<int, List<int>> children, Dictionary<int, int> depth)
        MakeTree(int root, params (int Parent, int[] Children)[] edges)
    {
        var children = new Dictionary<int, List<int>>();
        var depth    = new Dictionary<int, int>();

        // Collect all node ids
        var allIds = new HashSet<int> { root };
        foreach (var (p, cs) in edges) { allIds.Add(p); foreach (var c in cs) allIds.Add(c); }
        foreach (var id in allIds) children[id] = [];

        // Build children map and assign depths via BFS
        foreach (var (p, cs) in edges)
            foreach (var c in cs)
                children[p].Add(c);

        var queue = new Queue<int>();
        queue.Enqueue(root);
        depth[root] = 0;
        while (queue.Count > 0)
        {
            int cur = queue.Dequeue();
            foreach (var ch in children[cur])
                if (!depth.ContainsKey(ch))
                {
                    depth[ch] = depth[cur] + 1;
                    queue.Enqueue(ch);
                }
        }

        return (children, depth);
    }

    private static Dictionary<int, int> Assign(int root,
        Dictionary<int, List<int>> children, Dictionary<int, int> depth)
    {
        var colOut = new Dictionary<int, int>();
        TreeLayoutEngine.AssignColumns(root, children, depth, colOut);
        return colOut;
    }

    // ── Single node ──────────────────────────────────────────────────────

    [Fact]
    public void SingleNode_AssignedColumnZero()
    {
        var (ch, d) = MakeTree(0);
        var cols = Assign(0, ch, d);
        Assert.Equal(0, cols[0]);
    }

    // ── Linear chain ─────────────────────────────────────────────────────

    [Fact]
    public void LinearChain_AllNodesInSameColumn()
    {
        // 0 → 1 → 2 → 3
        var (ch, d) = MakeTree(0,
            (0, [1]), (1, [2]), (2, [3]));

        var cols = Assign(0, ch, d);

        // Single-child chain: all should be in column 0
        Assert.Equal(0, cols[0]);
        Assert.Equal(0, cols[1]);
        Assert.Equal(0, cols[2]);
        Assert.Equal(0, cols[3]);
    }

    // ── Two children ─────────────────────────────────────────────────────

    [Fact]
    public void TwoChildren_SiblingColumnsAreDifferent()
    {
        // 0 → {1, 2}
        var (ch, d) = MakeTree(0, (0, [1, 2]));
        var cols = Assign(0, ch, d);

        Assert.NotEqual(cols[1], cols[2]);
    }

    [Fact]
    public void TwoChildren_NoColumnCollisionOnSameRow()
    {
        var (ch, d) = MakeTree(0, (0, [1, 2]));
        var cols = Assign(0, ch, d);

        // Siblings must be in different columns
        var byRow = cols.GroupBy(kv => d[kv.Key], kv => kv.Value);
        foreach (var row in byRow)
        {
            var rowCols = row.ToList();
            Assert.Equal(rowCols.Count, rowCols.Distinct().Count());
        }
    }

    [Fact]
    public void TwoChildren_ParentCenteredOverChildren()
    {
        // 0 → {1, 2}: parent at col (c1+c2)/2
        var (ch, d) = MakeTree(0, (0, [1, 2]));
        var cols = Assign(0, ch, d);

        int expected = (cols[1] + cols[2]) / 2;
        Assert.Equal(expected, cols[0]);
    }

    // ── Wider tree ────────────────────────────────────────────────────────

    [Fact]
    public void ThreeChildren_AllSiblingsInDifferentColumns()
    {
        var (ch, d) = MakeTree(0, (0, [1, 2, 3]));
        var cols = Assign(0, ch, d);

        Assert.Equal(3, new[] { cols[1], cols[2], cols[3] }.Distinct().Count());
    }

    [Fact]
    public void ThreeChildren_Ordered()
    {
        // Children should be assigned left-to-right in the order they appear
        var (ch, d) = MakeTree(0, (0, [1, 2, 3]));
        var cols = Assign(0, ch, d);

        Assert.True(cols[1] < cols[2]);
        Assert.True(cols[2] < cols[3]);
    }

    // ── Balanced binary tree ──────────────────────────────────────────────

    [Fact]
    public void BalancedBinaryTree_NoCollisions()
    {
        //        0
        //      /   \
        //     1     2
        //    / \   / \
        //   3   4 5   6
        var (ch, d) = MakeTree(0,
            (0, [1, 2]),
            (1, [3, 4]),
            (2, [5, 6]));

        var cols = Assign(0, ch, d);

        // Check no two nodes on the same row share a column
        var byRow = new Dictionary<int, List<int>>();
        foreach (var (id, col) in cols)
        {
            int row = d[id];
            if (!byRow.ContainsKey(row)) byRow[row] = [];
            byRow[row].Add(col);
        }
        foreach (var (_, rowCols) in byRow)
            Assert.Equal(rowCols.Count, rowCols.Distinct().Count());
    }

    [Fact]
    public void BalancedBinaryTree_RootCenteredOverLeaves()
    {
        var (ch, d) = MakeTree(0,
            (0, [1, 2]),
            (1, [3, 4]),
            (2, [5, 6]));

        var cols = Assign(0, ch, d);

        // Root should be centered over the four leaves
        int leafMin = new[] { cols[3], cols[4], cols[5], cols[6] }.Min();
        int leafMax = new[] { cols[3], cols[4], cols[5], cols[6] }.Max();
        int expectedRoot = (leafMin + leafMax) / 2;
        Assert.Equal(expectedRoot, cols[0]);
    }

    // ── Non-negative columns ──────────────────────────────────────────────

    [Fact]
    public void AllColumns_NonNegative()
    {
        var (ch, d) = MakeTree(0,
            (0, [1, 2]),
            (1, [3, 4]),
            (2, [5, 6]));

        var cols = Assign(0, ch, d);

        Assert.All(cols.Values, c => Assert.True(c >= 0));
    }

    // ── Subtree gap ───────────────────────────────────────────────────────

    [Fact]
    public void AdjacentSubtrees_SeparatedByAtLeastOneColumn()
    {
        // Two independent subtrees under root: left subtree (1→{3,4}), right (2→{5,6})
        var (ch, d) = MakeTree(0,
            (0, [1, 2]),
            (1, [3, 4]),
            (2, [5, 6]));

        var cols = Assign(0, ch, d);

        // Rightmost leaf of left subtree vs leftmost leaf of right subtree
        int rightmostLeft  = Math.Max(cols[3], cols[4]);
        int leftmostRight  = Math.Min(cols[5], cols[6]);
        Assert.True(leftmostRight > rightmostLeft,
            $"Expected gap between subtrees; got rightmost left={rightmostLeft}, leftmost right={leftmostRight}");
    }

    // ── Deep chain with branch ────────────────────────────────────────────

    [Fact]
    public void DeepChainWithBranch_NoCollisions()
    {
        // 0→1→2→{3,4}  with extra chain 0→5→6
        var (ch, d) = MakeTree(0,
            (0, [1, 5]),
            (1, [2]),
            (2, [3, 4]),
            (5, [6]));

        var cols = Assign(0, ch, d);

        var byRow = new Dictionary<int, List<int>>();
        foreach (var (id, col) in cols)
        {
            int row = d[id];
            if (!byRow.ContainsKey(row)) byRow[row] = [];
            byRow[row].Add(col);
        }
        foreach (var (_, rowCols) in byRow)
            Assert.Equal(rowCols.Count, rowCols.Distinct().Count());
    }
}
