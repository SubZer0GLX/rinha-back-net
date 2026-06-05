using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using Fraud.Core;
using Fraud.IndexBuilder;

// Usage: Fraud.IndexBuilder <references.json.gz> <out index.bin> [nlist] [trainSample] [iters]
//
// Reads the labelled reference vectors, quantizes them to int8, trains an IVF
// (k-means) coarse index and writes a compact index.bin consumed by the API.

if (args.Length < 2)
{
    Console.Error.WriteLine("usage: IndexBuilder <references.json.gz> <out.bin> [nlist=2048] [trainSample=150000] [iters=15]");
    return 1;
}

string inPath = args[0];
string outPath = args[1];
int nlist = args.Length > 2 ? int.Parse(args[2]) : 2048;
int trainSample = args.Length > 3 ? int.Parse(args[3]) : 150_000;
int iters = args.Length > 4 ? int.Parse(args[4]) : 15;

const int Dim = Quantizer.Dim;
var sw = Stopwatch.StartNew();

// ---- 1. decompress + stream-parse vectors -> quantized bytes + fraud bits ----
Console.WriteLine($"[{sw.Elapsed}] reading {inPath} ...");
byte[] raw = DecompressAll(inPath);
Console.WriteLine($"[{sw.Elapsed}] decompressed {raw.Length / (1024 * 1024)} MB, parsing ...");

var (vectors, fraudBits, count) = ParseReferences(raw, Dim);
raw = null!;
GC.Collect();
Console.WriteLine($"[{sw.Elapsed}] parsed {count:N0} vectors");

// ---- 2. train IVF k-means ----
if (nlist > count) nlist = Math.Max(1, count);
Console.WriteLine($"[{sw.Elapsed}] training k-means: nlist={nlist}, sample={Math.Min(trainSample, count):N0}, iters={iters}");
float[] centroidsF = KMeans.Train(vectors, count, Dim, nlist, trainSample, iters, sw);

// ---- 3. assign every vector to a cluster ----
Console.WriteLine($"[{sw.Elapsed}] assigning {count:N0} vectors to clusters ...");
int[] assign = new int[count];
KMeans.AssignAll(vectors, count, Dim, centroidsF, nlist, assign);

// ---- 4. reorder vectors/labels so each cluster is contiguous ----
Console.WriteLine($"[{sw.Elapsed}] reordering ...");
int[] listOffsets = new int[nlist + 1];
foreach (int a in assign) listOffsets[a + 1]++;
for (int i = 0; i < nlist; i++) listOffsets[i + 1] += listOffsets[i];

ushort[] reordered = new ushort[(long)count * Dim];
byte[] labelBits = new byte[(count + 7) / 8];
int[] cursor = (int[])listOffsets.Clone();
for (int i = 0; i < count; i++)
{
    int dst = cursor[assign[i]]++;
    Array.Copy(vectors, (long)i * Dim, reordered, (long)dst * Dim, Dim);
    if ((fraudBits[i >> 3] & (1 << (i & 7))) != 0)
        labelBits[dst >> 3] |= (byte)(1 << (dst & 7));
}

// ---- 5. quantize centroids and write ----
ushort[] centroidsB = new ushort[nlist * Dim];
for (int i = 0; i < nlist * Dim; i++)
    centroidsB[i] = (ushort)Math.Clamp((int)MathF.Round(centroidsF[i]), 0, Quantizer.MaxLevel);

Console.WriteLine($"[{sw.Elapsed}] writing {outPath} ...");
using (var fs = File.Create(outPath))
{
    IndexFormat.Write(fs, count, nlist, centroidsB, listOffsets, reordered, labelBits);
}

long fraudCount = 0;
for (int i = 0; i < count; i++) if ((labelBits[i >> 3] & (1 << (i & 7))) != 0) fraudCount++;
Console.WriteLine($"[{sw.Elapsed}] done. {count:N0} vectors ({fraudCount:N0} fraud), "
    + $"index size {new FileInfo(outPath).Length / (1024 * 1024)} MB");
return 0;


static byte[] DecompressAll(string path)
{
    using var file = File.OpenRead(path);
    using var gz = new GZipStream(file, CompressionMode.Decompress);
    using var ms = new MemoryStream(capacity: 320 * 1024 * 1024);
    gz.CopyTo(ms, 1 << 20);
    // hand back exactly Length bytes
    if (ms.Length == ms.GetBuffer().Length) return ms.GetBuffer();
    return ms.ToArray();
}

// Parses an array of { "vector":[14 floats], "label":"fraud"|"legit" }.
// Centroid coords are stored already quantized (bytes) to keep memory at ~42MB.
static (ushort[] vectors, byte[] fraudBits, int count) ParseReferences(byte[] utf8, int dim)
{
    // first pass would be needed to size exactly; instead grow geometrically
    int cap = 4_000_000;
    ushort[] vectors = new ushort[(long)cap * dim];
    byte[] fraudBits = new byte[(cap + 7) / 8];
    int count = 0;

    var reader = new Utf8JsonReader(utf8, isFinalBlock: true, state: default);
    Span<float> tmp = stackalloc float[dim];

    // expect StartArray
    reader.Read(); // StartArray
    while (reader.Read() && reader.TokenType == JsonTokenType.StartObject)
    {
        int di = 0;
        bool fraud = false;
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            if (reader.ValueTextEquals("vector"))
            {
                reader.Read(); // StartArray
                di = 0;
                while (reader.Read() && reader.TokenType == JsonTokenType.Number)
                    tmp[di++] = reader.GetSingle();
                // reader now at EndArray
            }
            else if (reader.ValueTextEquals("label"))
            {
                reader.Read();
                fraud = reader.ValueTextEquals("fraud");
            }
            else
            {
                reader.Read();
                reader.Skip();
            }
        }
        // reader at EndObject

        if (count >= cap)
        {
            cap *= 2;
            Array.Resize(ref vectors, (int)Math.Min((long)cap * dim, int.MaxValue - 64));
            Array.Resize(ref fraudBits, (cap + 7) / 8);
        }

        long off = (long)count * dim;
        for (int k = 0; k < dim; k++)
            vectors[off + k] = Quantizer.Encode(tmp[k]);
        if (fraud) fraudBits[count >> 3] |= (byte)(1 << (count & 7));
        count++;
    }

    return (vectors, fraudBits, count);
}
