namespace Fraud.Core;

/// <summary>
/// Maps vector components from the spec range [-1, 1] to a 16-bit value [0, 65535].
/// Both reference vectors and query vectors pass through the same affine map, so
/// Euclidean ordering is preserved (uniform scale) and the -1 sentinel (used for
/// null last_transaction) lands cleanly at 0. 16-bit resolution keeps quantization
/// error ~256x smaller than int8, so the k-NN result matches exact float search on
/// almost all boundary cases.
/// </summary>
public static class Quantizer
{
    public const int Dim = 14;
    public const int MaxLevel = 65535;

    public static ushort Encode(float v)
    {
        // v in [-1, 1] -> [0, 65535]
        float t = (v + 1f) * 0.5f * MaxLevel;
        if (t < 0f) t = 0f;
        else if (t > MaxLevel) t = MaxLevel;
        return (ushort)(t + 0.5f);
    }

    public static void Encode(ReadOnlySpan<float> src, Span<ushort> dst)
    {
        for (int i = 0; i < Dim; i++)
            dst[i] = Encode(src[i]);
    }
}
