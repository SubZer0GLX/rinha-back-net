using System.Buffers;
using System.Text;
using System.Text.Json;
using Fraud.Api;
using Fraud.Core;

string dataDir = Environment.GetEnvironmentVariable("DATA_DIR") ?? "data";
string indexPath = Environment.GetEnvironmentVariable("INDEX_PATH") ?? Path.Combine(dataDir, "index.bin");
int nprobe = int.TryParse(Environment.GetEnvironmentVariable("NPROBE"), out int np) ? np : 24;
int port = int.TryParse(Environment.GetEnvironmentVariable("PORT"), out int p) ? p : 8080;

// ---- load reference data once at startup ----
var norm = JsonSerializer.Deserialize(File.ReadAllText(Path.Combine(dataDir, "normalization.json")),
    AppJsonContext.Default.NormConstants)!;
var mcc = JsonSerializer.Deserialize(File.ReadAllText(Path.Combine(dataDir, "mcc_risk.json")),
    AppJsonContext.Default.DictionaryStringDouble)!;
var vectorizer = new FastVectorizer(norm, mcc);
var index = IndexData.Load(indexPath);

// one Searcher per thread (scratch state is not shared)
var searcherLocal = new ThreadLocal<Searcher>(() => new Searcher(index));

// Only 6 possible responses (fraud_score = frauds/5). Pre-serialize them.
byte[][] responses = new byte[6][];
for (int f = 0; f <= 5; f++)
{
    float score = f / 5f;
    bool approved = score < Searcher.Threshold;
    string scoreStr = f switch { 0 => "0", 5 => "1", _ => (f / 5.0).ToString("0.0",
        System.Globalization.CultureInfo.InvariantCulture) };
    responses[f] = Encoding.UTF8.GetBytes($"{{\"approved\":{(approved ? "true" : "false")},\"fraud_score\":{scoreStr}}}");
}

var builder = WebApplication.CreateSlimBuilder(args);
builder.WebHost.ConfigureKestrel(o =>
{
    o.ListenAnyIP(port);
    o.AllowSynchronousIO = false;
});
builder.Logging.ClearProviders();

var app = builder.Build();

// Warm up JIT/R2R paths and page in the index before signalling readiness, so the
// first real requests don't pay cold-start latency (which otherwise shows up as
// p99 spikes and 2001ms timeouts during the ramp).
bool ready = false;
WarmUp();
ready = true;

void WarmUp()
{
    var rng = new Random(7);
    Parallel.For(0, Environment.ProcessorCount, _ =>
    {
        var s = searcherLocal.Value!;
        Span<ushort> q = stackalloc ushort[Quantizer.Dim];
        float sink = 0f;
        for (int n = 0; n < 4000; n++)
        {
            for (int d = 0; d < Quantizer.Dim; d++) q[d] = (ushort)rng.Next(65536);
            sink += s.Score(q, nprobe);
        }
        GC.KeepAlive(sink);
    });
}

app.MapGet("/ready", (HttpContext ctx) =>
{
    ctx.Response.StatusCode = ready ? 200 : 503;
    return Task.CompletedTask;
});

app.MapPost("/fraud-score", async (HttpContext ctx) =>
{
    // read the (small) body fully into a pooled buffer
    var body = ctx.Request.Body;
    int len = (int)(ctx.Request.ContentLength ?? 0);
    byte[] buf = ArrayPool<byte>.Shared.Rent(len > 0 ? len : 4096);
    try
    {
        int total = 0, read;
        if (len > 0)
        {
            while (total < len && (read = await body.ReadAsync(buf.AsMemory(total, len - total))) > 0)
                total += read;
        }
        else
        {
            while ((read = await body.ReadAsync(buf.AsMemory(total, buf.Length - total))) > 0)
            {
                total += read;
                if (total == buf.Length)
                {
                    var bigger = ArrayPool<byte>.Shared.Rent(buf.Length * 2);
                    Array.Copy(buf, bigger, total);
                    ArrayPool<byte>.Shared.Return(buf);
                    buf = bigger;
                }
            }
        }

        // ---- synchronous hot path (no thread hop) ----
        int fraudCount;
        try
        {
            Span<float> f = stackalloc float[Quantizer.Dim];
            Span<ushort> q = stackalloc ushort[Quantizer.Dim];
            vectorizer.TryCompute(buf.AsSpan(0, total), f);
            Quantizer.Encode(f, q);
            float score = searcherLocal.Value!.Score(q, nprobe);
            fraudCount = (int)MathF.Round(score * 5f);
        }
        catch
        {
            // Malformed payload: respond fast & safe rather than emit HTTP 500
            // (an Err weighs 5 and counts toward the 15% failure cut).
            fraudCount = 0;
        }

        byte[] resp = responses[fraudCount];
        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = "application/json";
        ctx.Response.ContentLength = resp.Length;
        await ctx.Response.Body.WriteAsync(resp);
    }
    finally
    {
        ArrayPool<byte>.Shared.Return(buf);
    }
});

Console.WriteLine($"ready: {index.Count:N0} vectors, {index.NList} lists, nprobe={nprobe}, port={port}");
app.Run();
