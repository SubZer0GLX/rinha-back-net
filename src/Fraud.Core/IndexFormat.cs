using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Fraud.Core;

/// <summary>
/// Binary layout of index.bin (little-endian); vectors/centroids are 16-bit (ushort):
///
///   magic   int32  = 0x46524432 ("FRD2")
///   count   int32  = N (number of reference vectors)
///   dim     int32  = 14
///   nlist   int32  = number of IVF clusters
///   centroids   nlist * dim  ushort  (quantized centroid coordinates)
///   listOffsets (nlist+1) * int32     (prefix sums into the reordered arrays)
///   vectors     N * dim  ushort        (reordered so each cluster is contiguous)
///   labels      ceil(N/8) bytes        (bit set => fraud), reordered same as vectors
///
/// Vectors are grouped by cluster: cluster c occupies
/// [listOffsets[c], listOffsets[c+1]) in the reordered vector/label arrays.
/// </summary>
public static class IndexFormat
{
    public const int Magic = 0x46524432;
    public const int HeaderBytes = 16;

    public static void Write(
        Stream stream,
        int count,
        int nlist,
        ReadOnlySpan<ushort> centroids,  // nlist * Dim
        ReadOnlySpan<int> listOffsets,   // nlist + 1
        ReadOnlySpan<ushort> vectors,    // count * Dim
        ReadOnlySpan<byte> labelBits)    // ceil(count/8)
    {
        Span<byte> hdr = stackalloc byte[HeaderBytes];
        BinaryPrimitives.WriteInt32LittleEndian(hdr[0..], Magic);
        BinaryPrimitives.WriteInt32LittleEndian(hdr[4..], count);
        BinaryPrimitives.WriteInt32LittleEndian(hdr[8..], Quantizer.Dim);
        BinaryPrimitives.WriteInt32LittleEndian(hdr[12..], nlist);
        stream.Write(hdr);

        stream.Write(MemoryMarshal.AsBytes(centroids));

        Span<byte> off = stackalloc byte[4];
        for (int i = 0; i < listOffsets.Length; i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(off, listOffsets[i]);
            stream.Write(off);
        }

        stream.Write(MemoryMarshal.AsBytes(vectors));
        stream.Write(labelBits);
    }
}

/// <summary>
/// Read-only in-memory view over a loaded index.bin.
/// The whole byte buffer is held once; ushort spans are reinterpreted over it.
/// </summary>
public sealed class IndexData
{
    public int Count { get; }
    public int Dim { get; }
    public int NList { get; }

    private readonly byte[] _buffer;
    private readonly int _centroidsOffset; // byte offset
    private readonly int _listOffsetsOffset;
    private readonly int _vectorsOffset;
    private readonly int _labelsOffset;

    public IndexData(byte[] buffer)
    {
        _buffer = buffer;
        var span = buffer.AsSpan();

        int magic = BinaryPrimitives.ReadInt32LittleEndian(span[0..]);
        if (magic != IndexFormat.Magic)
            throw new InvalidDataException($"Bad index magic: 0x{magic:X8}");

        Count = BinaryPrimitives.ReadInt32LittleEndian(span[4..]);
        Dim = BinaryPrimitives.ReadInt32LittleEndian(span[8..]);
        NList = BinaryPrimitives.ReadInt32LittleEndian(span[12..]);

        _centroidsOffset = IndexFormat.HeaderBytes;
        _listOffsetsOffset = _centroidsOffset + NList * Dim * sizeof(ushort);
        _vectorsOffset = _listOffsetsOffset + (NList + 1) * sizeof(int);
        _labelsOffset = _vectorsOffset + Count * Dim * sizeof(ushort);
    }

    public ReadOnlySpan<ushort> Vectors =>
        MemoryMarshal.Cast<byte, ushort>(_buffer.AsSpan(_vectorsOffset, Count * Dim * sizeof(ushort)));

    public ReadOnlySpan<ushort> CentroidAt(int c) =>
        MemoryMarshal.Cast<byte, ushort>(_buffer.AsSpan(_centroidsOffset + c * Dim * sizeof(ushort), Dim * sizeof(ushort)));

    public int ListStart(int c) =>
        BinaryPrimitives.ReadInt32LittleEndian(_buffer.AsSpan(_listOffsetsOffset + c * sizeof(int)));

    public int ListEnd(int c) =>
        BinaryPrimitives.ReadInt32LittleEndian(_buffer.AsSpan(_listOffsetsOffset + (c + 1) * sizeof(int)));

    public bool IsFraud(int i)
    {
        byte b = _buffer[_labelsOffset + (i >> 3)];
        return (b & (1 << (i & 7))) != 0;
    }

    public static IndexData Load(string path) => new(File.ReadAllBytes(path));
}
