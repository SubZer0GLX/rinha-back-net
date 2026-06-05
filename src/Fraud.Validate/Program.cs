using System.Diagnostics;
using System.Text.Json;
using Fraud.Core;

// Usage: Fraud.Validate <index.bin> <test-data.json> <normalization.json> <mcc_risk.json> [nprobeList]
//
// Replays every labelled test payload through vectorize -> quantize -> k-NN and
// compares approved against expected_approved, for several nprobe settings.
// Reports the confusion matrix and the detection score from AVALIACAO.md.

if (args.Length < 4)
{
    Console.Error.WriteLine("usage: Validate <index.bin> <test-data.json> <normalization.json> <mcc_risk.json> [nprobe,csv]");
    return 1;
}

string indexPath = args[0], testPath = args[1], normPath = args[2], mccPath = args[3];
int[] nprobes = args.Length > 4
    ? args[4].Split(',').Select(int.Parse).ToArray()
    : new[] { 8, 16, 32, 64, int.MaxValue };

var sw = Stopwatch.StartNew();

var norm = JsonSerializer.Deserialize<NormConstants>(File.ReadAllText(normPath))!;
var mcc = JsonSerializer.Deserialize<Dictionary<string, double>>(File.ReadAllText(mccPath))!;
var vec = new Vectorizer(norm, mcc);

Console.WriteLine($"[{sw.Elapsed}] loading index {indexPath} ...");
var idx = IndexData.Load(indexPath);
Console.WriteLine($"[{sw.Elapsed}] index: {idx.Count:N0} vectors, {idx.NList} lists");

// load test payloads + expected labels
Console.WriteLine($"[{sw.Elapsed}] loading test data {testPath} ...");
using var doc = JsonDocument.Parse(File.ReadAllText(testPath));
var entriesEl = doc.RootElement.GetProperty("entries");
int n = entriesEl.GetArrayLength();
var requests = new FraudRequest[n];
var rawJson = new byte[n][];
var expected = new bool[n];
int j = 0;
foreach (var e in entriesEl.EnumerateArray())
{
    var reqEl = e.GetProperty("request");
    requests[j] = reqEl.Deserialize<FraudRequest>()!;
    rawJson[j] = System.Text.Encoding.UTF8.GetBytes(reqEl.GetRawText());
    expected[j] = e.GetProperty("expected_approved").GetBoolean();
    j++;
}
Console.WriteLine($"[{sw.Elapsed}] loaded {n:N0} test payloads");

// precompute quantized queries once; cross-check FastVectorizer vs Vectorizer
var fast = new FastVectorizer(norm, mcc);
var queries = new ushort[n][];
float[] fbuf = new float[Quantizer.Dim];
float[] fbuf2 = new float[Quantizer.Dim];
int mismatches = 0;
ushort[] q2 = new ushort[Quantizer.Dim];
for (int i = 0; i < n; i++)
{
    vec.Compute(requests[i], fbuf);
    var q = new ushort[Quantizer.Dim];
    Quantizer.Encode(fbuf, q);
    queries[i] = q;

    fast.TryCompute(rawJson[i], fbuf2);
    Quantizer.Encode(fbuf2, q2);
    if (!q2.AsSpan().SequenceEqual(q)) mismatches++;
}
Console.WriteLine($"[{sw.Elapsed}] FastVectorizer vs Vectorizer quantized mismatches: {mismatches} / {n}");

Console.WriteLine();
Console.WriteLine($"{"nprobe",-8} {"TP",7} {"TN",7} {"FP",7} {"FN",7} {"fail%",8} {"det_score",10} {"ms/q",8}");
var tlSearcher = new ThreadLocal<Searcher>(() => new Searcher(idx), trackAllValues: false);
foreach (int np in nprobes)
{
    int tp = 0, tn = 0, fp = 0, fn = 0;
    var t0 = Stopwatch.GetTimestamp();
    Parallel.For(0, n, () => (tp: 0, tn: 0, fp: 0, fn: 0), (i, _, acc) =>
    {
        float score = tlSearcher.Value!.Score(queries[i], np);
        bool approved = score < Searcher.Threshold;
        bool exp = expected[i];
        if (approved == exp) { if (approved) acc.tn++; else acc.tp++; }
        else { if (approved) acc.fn++; else acc.fp++; }
        return acc;
    }, acc =>
    {
        Interlocked.Add(ref tp, acc.tp); Interlocked.Add(ref tn, acc.tn);
        Interlocked.Add(ref fp, acc.fp); Interlocked.Add(ref fn, acc.fn);
    });
    double ms = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;

    int E = fp * 1 + fn * 3;
    double eps = (double)E / n;
    double failRate = (double)(fp + fn) / n;
    double det = failRate > 0.15
        ? -3000
        : 1000 * Math.Log10(1.0 / Math.Max(eps, 0.001)) - 300 * Math.Log10(1 + E);

    string label = np == int.MaxValue ? "exact" : np.ToString();
    Console.WriteLine($"{label,-8} {tp,7} {tn,7} {fp,7} {fn,7} {failRate * 100,7:F2}% {det,10:F1} {ms / n,8:F3}");
}
return 0;
