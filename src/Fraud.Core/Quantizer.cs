namespace Fraud.Core;

/// <summary>
/// Maps vector components from the spec range [-1, 1] to a byte [0, 255].
/// Both reference vectors and query vectors pass through the same affine map,
/// so Euclidean ordering is preserved (uniform scale) and the -1 sentinel
/// (used for null last_transaction) lands cleanly at byte 0.
/// </summary>
public static class Quantizer
{
    public const int Dim = 14;

    public static byte Encode(float v)
    {
        // v in [-1, 1] -> [0, 255]
        float t = (v + 1f) * 0.5f * 255f;
        if (t < 0f) t = 0f;
        else if (t > 255f) t = 255f;
        return (byte)(t + 0.5f);
    }

    public static void Encode(ReadOnlySpan<float> src, Span<byte> dst)
    {
        for (int i = 0; i < Dim; i++)
            dst[i] = Encode(src[i]);
    }
}
