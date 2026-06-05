using System.Runtime.CompilerServices;

namespace Fraud.Core;

/// <summary>
/// k-NN (k=5) fraud scoring over the quantized IVF index.
/// One instance per request thread (holds small scratch buffers); cheap to allocate
/// but intended to be pooled/thread-static for the hot path.
/// </summary>
public sealed class Searcher
{
    public const int K = 5;
    public const float Threshold = 0.6f;

    private readonly IndexData _idx;

    // scratch for the k best: parallel arrays kept sorted ascending by distance
    private readonly int[] _bestDist = new int[K];
    private readonly int[] _bestPos = new int[K];

    // scratch for centroid ranking
    private readonly int[] _centDist;
    private readonly int[] _centOrder;

    public Searcher(IndexData idx)
    {
        _idx = idx;
        _centDist = new int[idx.NList];
        _centOrder = new int[idx.NList];
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static int SquaredDistance(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        // 14 dimensions; int accumulation (max 14 * 255^2 ≈ 910k, no overflow).
        int sum = 0;
        for (int i = 0; i < Quantizer.Dim; i++)
        {
            int d = a[i] - b[i];
            sum += d * d;
        }
        return sum;
    }

    /// <summary>
    /// Returns the fraud score (frauds among the 5 nearest / 5) for a quantized query.
    /// nprobe = number of nearest clusters to scan; pass int.MaxValue for exact brute force.
    /// </summary>
    public float Score(ReadOnlySpan<byte> query, int nprobe)
    {
        int k = 0;
        for (int i = 0; i < K; i++) { _bestDist[i] = int.MaxValue; _bestPos[i] = -1; }

        int nlist = _idx.NList;
        if (nlist <= 1 || nprobe >= nlist)
        {
            ScanRange(query, 0, _idx.Count, ref k);
        }
        else
        {
            // rank centroids by distance, take nprobe nearest
            for (int c = 0; c < nlist; c++)
            {
                _centDist[c] = SquaredDistance(query, _idx.CentroidAt(c));
                _centOrder[c] = c;
            }
            PartialSortCentroids(nprobe);

            for (int p = 0; p < nprobe; p++)
            {
                int c = _centOrder[p];
                ScanRange(query, _idx.ListStart(c), _idx.ListEnd(c), ref k);
            }
        }

        // count frauds among the (up to K) collected neighbours
        int frauds = 0;
        for (int i = 0; i < K; i++)
            if (_bestPos[i] >= 0 && _idx.IsFraud(_bestPos[i])) frauds++;

        return frauds / (float)K;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void ScanRange(ReadOnlySpan<byte> query, int start, int end, ref int _)
    {
        var vectors = _idx.Vectors;
        int dim = Quantizer.Dim;
        for (int i = start; i < end; i++)
        {
            int dist = SquaredDistance(query, vectors.Slice(i * dim, dim));
            // insert into the sorted top-K if it beats the worst
            if (dist < _bestDist[K - 1])
            {
                int j = K - 1;
                while (j > 0 && _bestDist[j - 1] > dist)
                {
                    _bestDist[j] = _bestDist[j - 1];
                    _bestPos[j] = _bestPos[j - 1];
                    j--;
                }
                _bestDist[j] = dist;
                _bestPos[j] = i;
            }
        }
    }

    // selection of the nprobe nearest centroids into the front of _centOrder
    private void PartialSortCentroids(int nprobe)
    {
        int n = _idx.NList;
        for (int p = 0; p < nprobe; p++)
        {
            int best = p;
            for (int q = p + 1; q < n; q++)
                if (_centDist[_centOrder[q]] < _centDist[_centOrder[best]]) best = q;
            (_centOrder[p], _centOrder[best]) = (_centOrder[best], _centOrder[p]);
        }
    }
}
