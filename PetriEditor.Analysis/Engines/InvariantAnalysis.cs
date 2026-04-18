namespace Analysis.Engines;

/// <summary>
/// Computes P-invariants (place invariants, y·W = 0, y ≥ 0) and
/// T-invariants (transition invariants, W·x = 0, x ≥ 0) via the
/// Farkas / support-enumeration algorithm.
/// </summary>
public sealed class InvariantAnalysis
{
    /// <summary>Maximum candidate vectors kept per Farkas elimination step.</summary>
    public const int MaxCandidates = 500;

    public bool HasErrors    { get; private set; }
    public bool WasSkipped   { get; private set; }
    public bool WasTruncated { get; private set; }
    public string? ErrorMsg  { get; private set; }

    public IReadOnlyList<Invariant> PInvariants { get; private set; } = [];
    public IReadOnlyList<Invariant> TInvariants { get; private set; } = [];

    /// <summary>
    /// Marks the invariant computation as skipped because the net is unbounded.
    /// P-invariants are only meaningful for bounded nets (they represent token-conservation laws).
    /// </summary>
    public void SkipUnbounded()
    {
        WasSkipped = true;
        HasErrors  = true;
        ErrorMsg   = "Net is unbounded — invariant computation skipped (P-invariants are only meaningful for bounded nets).";
    }

    public void Compute(Analysis.PetriNetSnapshot net)
    {
        HasErrors = false; ErrorMsg = null; WasTruncated = false;
        PInvariants = []; TInvariants = [];

        if (!net.Places.Any() || !net.Transitions.Any())
        { HasErrors = true; ErrorMsg = "Net has no places or transitions."; return; }

        try
        {
            int[,] W = net.IncidenceMatrix();
            int p = net.Places.Count, t = net.Transitions.Count;

            // P-invariants: y · W = 0  →  solve W^T · y = 0
            var pVecs = ComputeInvariants(Transpose(W, p, t), p);
            PInvariants = pVecs.Select(vec =>
                new Invariant(
                    net.Places.Select((pl, i) => (pl.Id, vec[i]))
                               .Where(x => x.Item2 > 0)
                               .ToDictionary(x => x.Id, x => x.Item2)
                )).ToList();

            // T-invariants: W · x = 0
            var tVecs = ComputeInvariants(W, t);
            TInvariants = tVecs.Select(vec =>
                new Invariant(
                    net.Transitions.Select((tr, i) => (tr.Id, vec[i]))
                                   .Where(x => x.Item2 > 0)
                                   .ToDictionary(x => x.Id, x => x.Item2)
                )).ToList();
        }
        catch (Exception ex)
        {
            HasErrors = true; ErrorMsg = ex.Message;
        }
    }

    // ── Farkas algorithm (support-based, non-negative solutions to A·x = 0) ──

    private List<int[]> ComputeInvariants(int[,] A, int cols)
    {
        var candidates = new List<int[]>();
        for (int i = 0; i < cols; i++)
        { var v = new int[cols]; v[i] = 1; candidates.Add(v); }

        int rows = A.GetLength(0);
        for (int r = 0; r < rows; r++)
        {
            var pos = new List<int[]>();
            var neg = new List<int[]>();
            var zer = new List<int[]>();

            foreach (var v in candidates)
            {
                long dot = 0;
                for (int j = 0; j < cols; j++) dot += (long)A[r, j] * v[j];
                if (dot > 0) pos.Add(v);
                else if (dot < 0) neg.Add(v);
                else zer.Add(v);
            }

            var next = new List<int[]>(zer);
            foreach (var vp in pos)
                foreach (var vn in neg)
                {
                    long dotp = 0, dotn = 0;
                    for (int j = 0; j < cols; j++)
                    { dotp += (long)A[r, j] * vp[j]; dotn += (long)A[r, j] * vn[j]; }

                    long alpha = Math.Abs(dotn), beta = dotp;
                    var longCombined = new long[cols];
                    for (int j = 0; j < cols; j++) longCombined[j] = alpha * vp[j] + beta * vn[j];

                    long g = GcdArray(longCombined);
                    if (g > 1) for (int j = 0; j < cols; j++) longCombined[j] /= g;

                    // Narrow to int; if any entry no longer fits, drop this candidate.
                    var combined = new int[cols];
                    bool overflow = false;
                    for (int j = 0; j < cols; j++)
                    {
                        if (longCombined[j] > int.MaxValue || longCombined[j] < int.MinValue)
                        { overflow = true; break; }
                        combined[j] = (int)longCombined[j];
                    }
                    if (overflow) continue;

                    if (combined.Any(x => x > 0)) next.Add(combined);
                }

            candidates = Deduplicate(next);
            if (candidates.Count > MaxCandidates)
            {
                WasTruncated = true;
                candidates = candidates.Take(MaxCandidates).ToList();
            }
        }

        return candidates.Where(v => v.Any(x => x > 0)).ToList();
    }

    private static int[,] Transpose(int[,] A, int rows, int cols)
    {
        var T = new int[cols, rows];
        for (int r = 0; r < rows; r++)
            for (int c = 0; c < cols; c++)
                T[c, r] = A[r, c];
        return T;
    }

    private static long GcdArray(long[] v)
    {
        long g = 0;
        foreach (var x in v) g = Gcd(g, Math.Abs(x));
        return g == 0 ? 1 : g;
    }

    private static long Gcd(long a, long b) => b == 0 ? a : Gcd(b, a % b);

    private static List<int[]> Deduplicate(List<int[]> vecs)
    {
        var seen = new HashSet<int[]>(VecComparer.Instance);
        var result = new List<int[]>(vecs.Count);
        foreach (var v in vecs)
            if (seen.Add(v)) result.Add(v);
        return result;
    }

    private sealed class VecComparer : IEqualityComparer<int[]>
    {
        public static readonly VecComparer Instance = new();
        public bool Equals(int[]? x, int[]? y)
        {
            if (x is null || y is null) return x is null && y is null;
            if (x.Length != y.Length) return false;
            for (int i = 0; i < x.Length; i++) if (x[i] != y[i]) return false;
            return true;
        }
        public int GetHashCode(int[] v)
        {
            var h = new HashCode();
            foreach (var x in v) h.Add(x);
            return h.ToHashCode();
        }
    }
}

public sealed class Invariant(IReadOnlyDictionary<string, int> structure)
{
    /// Maps place/transition ID → coefficient (only non-zero entries stored)
    public IReadOnlyDictionary<string, int> Structure { get; } = structure;

    public bool Covers(string id) => Structure.TryGetValue(id, out int v) && v > 0;
}
