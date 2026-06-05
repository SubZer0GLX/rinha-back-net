using System.Diagnostics;
using Fraud.Core;

namespace Fraud.IndexBuilder;

/// <summary>Plain Lloyd k-means used to build the IVF coarse quantizer.</summary>
internal static class KMeans
{
    public static float[] Train(ushort[] vectors, int count, int dim, int nlist,
                                int trainSample, int iters, Stopwatch sw)
    {
        int sample = Math.Min(trainSample, count);
        var rng = new Random(12345);

        // pick `sample` row indices to train on
        int[] rows = new int[sample];
        for (int i = 0; i < sample; i++) rows[i] = rng.Next(count);

        // init centroids from `nlist` random sample rows
        float[] cent = new float[nlist * dim];
        for (int c = 0; c < nlist; c++)
        {
            long src = (long)rows[rng.Next(sample)] * dim;
            for (int k = 0; k < dim; k++) cent[c * dim + k] = vectors[src + k];
        }

        float[] sums = new float[nlist * dim];
        int[] counts = new int[nlist];
        int[] sampleAssign = new int[sample];

        for (int it = 0; it < iters; it++)
        {
            Array.Clear(sums);
            Array.Clear(counts);

            Parallel.For(0, sample, i =>
            {
                long off = (long)rows[i] * dim;
                int best = NearestFloat(vectors, off, cent, nlist, dim);
                sampleAssign[i] = best;
            });

            for (int i = 0; i < sample; i++)
            {
                int c = sampleAssign[i];
                long off = (long)rows[i] * dim;
                counts[c]++;
                for (int k = 0; k < dim; k++) sums[c * dim + k] += vectors[off + k];
            }

            for (int c = 0; c < nlist; c++)
            {
                if (counts[c] == 0)
                {
                    // reseed empty cluster on a random sample row
                    long src = (long)rows[rng.Next(sample)] * dim;
                    for (int k = 0; k < dim; k++) cent[c * dim + k] = vectors[src + k];
                }
                else
                {
                    float inv = 1f / counts[c];
                    for (int k = 0; k < dim; k++) cent[c * dim + k] = sums[c * dim + k] * inv;
                }
            }

            if (it == 0 || it == iters - 1)
                Console.WriteLine($"[{sw.Elapsed}]   kmeans iter {it + 1}/{iters}");
        }

        return cent;
    }

    public static void AssignAll(ushort[] vectors, int count, int dim,
                                 float[] cent, int nlist, int[] assign)
    {
        Parallel.For(0, count, i =>
        {
            long off = (long)i * dim;
            assign[i] = NearestFloat(vectors, off, cent, nlist, dim);
        });
    }

    private static int NearestFloat(ushort[] vectors, long off, float[] cent, int nlist, int dim)
    {
        int best = 0;
        float bestD = float.MaxValue;
        for (int c = 0; c < nlist; c++)
        {
            int cb = c * dim;
            float d = 0f;
            for (int k = 0; k < dim; k++)
            {
                float diff = vectors[off + k] - cent[cb + k];
                d += diff * diff;
            }
            if (d < bestD) { bestD = d; best = c; }
        }
        return best;
    }
}
