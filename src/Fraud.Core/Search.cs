using System.Runtime.CompilerServices;

namespace Fraud.Core;

/// <summary>
/// k-NN (k=5) fraud scoring over the quantized IVF index.
/// Not thread-safe (holds scratch buffers); use one instance per thread.
/// </summary>
public sealed class Searcher
{
    public const int K = 5;
    public const float Threshold = 0.6f;

    private readonly IndexData _idx;

    private readonly long[] _bestDist = new long[K];
    private readonly int[] _bestPos = new int[K];

    private readonly long[] _centDist;
    private readonly int[] _centOrder;

    public Searcher(IndexData idx)
    {
        _idx = idx;
        _centDist = new long[idx.NList];
        _centOrder = new int[idx.NList];
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static long SquaredDistance(ReadOnlySpan<ushort> a, ReadOnlySpan<ushort> b)
    {
        // 14 dims of 16-bit values; long accumulation (max 14 * 65535^2 ≈ 6.0e10).
        long sum = 0;
        for (int i = 0; i < Quantizer.Dim; i++)
        {
            int d = a[i] - b[i];
            sum += (long)d * d;
        }
        return sum;
    }

    /// <summary>
    /// Fraud score (frauds among the 5 nearest / 5) for a quantized query.
    /// nprobe = number of nearest clusters to scan; pass int.MaxValue for brute force.
    /// </summary>
    public float Score(ReadOnlySpan<ushort> query, int nprobe)
    {
        for (int i = 0; i < K; i++) { _bestDist[i] = long.MaxValue; _bestPos[i] = -1; }

        int nlist = _idx.NList;
        if (nlist <= 1 || nprobe >= nlist)
        {
            ScanRange(query, 0, _idx.Count);
        }
        else
        {
            for (int c = 0; c < nlist; c++)
            {
                _centDist[c] = SquaredDistance(query, _idx.CentroidAt(c));
                _centOrder[c] = c;
            }
            PartialSortCentroids(nprobe);

            for (int p = 0; p < nprobe; p++)
            {
                int c = _centOrder[p];
                ScanRange(query, _idx.ListStart(c), _idx.ListEnd(c));
            }
        }

        int frauds = 0;
        for (int i = 0; i < K; i++)
            if (_bestPos[i] >= 0 && _idx.IsFraud(_bestPos[i])) frauds++;

        return frauds / (float)K;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void ScanRange(ReadOnlySpan<ushort> query, int start, int end)
    {
        var vectors = _idx.Vectors;
        int dim = Quantizer.Dim;
        for (int i = start; i < end; i++)
        {
            long dist = SquaredDistance(query, vectors.Slice(i * dim, dim));
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
