using Analysis.Algorithms;

namespace PetriEditor.Tests;

public class GraphLayoutEngineTests
{
    private static GraphLayoutEngine.Result Run(int root, int[] nodes, (int, int)[] edges)
        => GraphLayoutEngine.Layout(root, nodes, edges);

    [Fact]
    public void SingleNode_LayerZero_ColZero()
    {
        var r = Run(0, [0], []);
        Assert.Equal(0, r.Layer[0]);
        Assert.Equal(0, r.Col[0]);
        Assert.Empty(r.BackEdges);
    }

    [Fact]
    public void LinearChain_LayersMatchDistance()
    {
        //  0 → 1 → 2 → 3
        var r = Run(0, [0, 1, 2, 3], [(0, 1), (1, 2), (2, 3)]);
        Assert.Equal(0, r.Layer[0]);
        Assert.Equal(1, r.Layer[1]);
        Assert.Equal(2, r.Layer[2]);
        Assert.Equal(3, r.Layer[3]);
        Assert.Empty(r.BackEdges);
    }

    [Fact]
    public void RootAlwaysOnTopLayer()
    {
        // Multiple nodes, root is 0
        var r = Run(0, [0, 1, 2, 3], [(0, 1), (0, 2), (1, 3), (2, 3)]);
        int minLayer = r.Layer.Values.Min();
        Assert.Equal(0, r.Layer[0]);
        Assert.Equal(minLayer, r.Layer[0]);
    }

    [Fact]
    public void Diamond_NodesOnCorrectLayers()
    {
        //      0
        //     / \
        //    1   2
        //     \ /
        //      3
        var r = Run(0, [0, 1, 2, 3], [(0, 1), (0, 2), (1, 3), (2, 3)]);
        Assert.Equal(0, r.Layer[0]);
        Assert.Equal(1, r.Layer[1]);
        Assert.Equal(1, r.Layer[2]);
        Assert.Equal(2, r.Layer[3]);
        Assert.Empty(r.BackEdges);
    }

    [Fact]
    public void Cycle_DetectedAsBackEdge()
    {
        //  0 → 1 → 2 ↩ back to 0
        var r = Run(0, [0, 1, 2], [(0, 1), (1, 2), (2, 0)]);
        Assert.Contains((2, 0), r.BackEdges);
        Assert.DoesNotContain((0, 1), r.BackEdges);
        Assert.DoesNotContain((1, 2), r.BackEdges);
    }

    [Fact]
    public void SelfLoop_IsBackEdge()
    {
        var r = Run(0, [0, 1], [(0, 1), (1, 1)]);
        Assert.Contains((1, 1), r.BackEdges);
    }

    [Fact]
    public void SameLayerEdge_IsBackEdge()
    {
        // 0 → 1, 0 → 2, 1 → 2 (sibling edge on layer 1)
        var r = Run(0, [0, 1, 2], [(0, 1), (0, 2), (1, 2)]);
        Assert.Equal(1, r.Layer[1]);
        Assert.Equal(1, r.Layer[2]);
        // 1 → 2 is layer-1 → layer-1 — a back-edge under our definition
        Assert.Contains((1, 2), r.BackEdges);
    }

    [Fact]
    public void ColumnsAreUniqueWithinLayer()
    {
        // Build a graph with many nodes per layer and verify no two share a col
        // 0 has four children, each with two children → layer 2 has 8 nodes
        var nodes = Enumerable.Range(0, 13).ToArray();
        var edges = new (int, int)[]
        {
            (0, 1), (0, 2), (0, 3), (0, 4),
            (1, 5), (1, 6),
            (2, 7), (2, 8),
            (3, 9), (3, 10),
            (4, 11), (4, 12),
        };
        var r = Run(0, nodes, edges);

        var byLayer = r.Col
            .GroupBy(kv => r.Layer[kv.Key])
            .ToDictionary(g => g.Key, g => g.Select(kv => kv.Value).ToList());

        foreach (var (_, cols) in byLayer)
            Assert.Equal(cols.Count, cols.Distinct().Count());
    }

    [Fact]
    public void UnreachableNode_StillPlaced()
    {
        // Node 9 has no path from root
        var r = Run(0, [0, 1, 9], [(0, 1)]);
        Assert.True(r.Layer.ContainsKey(9));
        Assert.True(r.Col.ContainsKey(9));
    }

    [Fact]
    public void LargeGraph_CompletesInReasonableTime()
    {
        // Guard against accidental O(n³): 500 nodes in a grid-like graph.
        var nodes = Enumerable.Range(0, 500).ToArray();
        var edges = new List<(int, int)>();
        for (int i = 0; i < 499; i++) edges.Add((i, i + 1));
        for (int i = 0; i + 10 < 500; i++) edges.Add((i, i + 10));

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r = Run(0, nodes, edges.ToArray());
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 2000, $"Layout took {sw.ElapsedMilliseconds}ms");
        Assert.Equal(500, r.Layer.Count);
        Assert.Equal(500, r.Col.Count);
    }

    [Fact]
    public void DenseGraph_NoQuadraticBlowup()
    {
        // 200 nodes, ~8 edges per node distributed across layers. Previous
        // List.Contains-based barycenter was O(n²·e); this asserts the fix.
        const int N = 200;
        var rnd = new Random(42);
        var nodes = Enumerable.Range(0, N).ToArray();
        var edges = new List<(int, int)>();
        for (int i = 0; i < N - 1; i++) edges.Add((i, i + 1));
        for (int i = 0; i < N; i++)
            for (int k = 0; k < 8; k++)
            {
                int j = rnd.Next(N);
                if (j != i) edges.Add((i, j));
            }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r = Run(0, nodes, edges.ToArray());
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 1000, $"Dense layout took {sw.ElapsedMilliseconds}ms");
        Assert.Equal(N, r.Layer.Count);
    }
}
