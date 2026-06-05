using System.Text;
using System.Text.Json;

namespace Fraud.Core;

/// <summary>
/// Allocation-free path: parses the POST /fraud-score payload straight from its
/// UTF-8 bytes into the 14-dim vector, without materializing any request objects
/// or strings. Cuts per-request CPU and GC pressure on the hot path.
/// Produces the same vector as <see cref="Vectorizer"/>.
/// </summary>
public sealed class FastVectorizer
{
    private readonly NormConstants _n;
    private readonly byte[][] _mccKeys;
    private readonly float[] _mccVals;
    private const float DefaultMccRisk = 0.5f;

    public FastVectorizer(NormConstants norm, IReadOnlyDictionary<string, double> mccRisk)
    {
        _n = norm;
        _mccKeys = new byte[mccRisk.Count][];
        _mccVals = new float[mccRisk.Count];
        int i = 0;
        foreach (var kv in mccRisk)
        {
            _mccKeys[i] = Encoding.UTF8.GetBytes(kv.Key);
            _mccVals[i] = (float)kv.Value;
            i++;
        }
    }

    private static float Clamp(double x)
    {
        if (double.IsNaN(x)) return 0f;
        if (x < 0.0) return 0f;
        if (x > 1.0) return 1f;
        return (float)x;
    }

    public bool TryCompute(ReadOnlySpan<byte> json, Span<float> v)
    {
        double amount = 0, custAvg = 0, merchAvg = 0, kmHome = 0, lastKm = 0;
        int installments = 0, tx24 = 0;
        bool isOnline = false, cardPresent = false;
        DateTimeOffset reqAt = default;
        bool hasLast = false;
        DateTimeOffset lastTs = default;
        float mccRisk = DefaultMccRisk;
        bool unknownMerchant = true;

        // scratch for known_merchants bytes (parsed before merchant.id)
        Span<byte> kmBuf = stackalloc byte[2048];
        Span<int> kmStart = stackalloc int[129];
        Span<int> kmLen = stackalloc int[129];
        int kmCount = 0, kmUsed = 0;

        var r = new Utf8JsonReader(json);
        if (!r.Read() || r.TokenType != JsonTokenType.StartObject) return false;

        while (r.Read() && r.TokenType == JsonTokenType.PropertyName)
        {
            if (r.ValueTextEquals("transaction"))
            {
                r.Read(); // StartObject
                while (r.Read() && r.TokenType == JsonTokenType.PropertyName)
                {
                    if (r.ValueTextEquals("amount")) { r.Read(); amount = r.GetDouble(); }
                    else if (r.ValueTextEquals("installments")) { r.Read(); installments = r.GetInt32(); }
                    else if (r.ValueTextEquals("requested_at")) { r.Read(); reqAt = r.GetDateTimeOffset(); }
                    else { r.Read(); r.Skip(); }
                }
            }
            else if (r.ValueTextEquals("customer"))
            {
                r.Read();
                while (r.Read() && r.TokenType == JsonTokenType.PropertyName)
                {
                    if (r.ValueTextEquals("avg_amount")) { r.Read(); custAvg = r.GetDouble(); }
                    else if (r.ValueTextEquals("tx_count_24h")) { r.Read(); tx24 = r.GetInt32(); }
                    else if (r.ValueTextEquals("known_merchants"))
                    {
                        r.Read(); // StartArray
                        while (r.Read() && r.TokenType == JsonTokenType.String)
                        {
                            var s = r.ValueSpan;
                            if (kmCount < 129 && !r.HasValueSequence && kmUsed + s.Length <= kmBuf.Length)
                            {
                                s.CopyTo(kmBuf.Slice(kmUsed));
                                kmStart[kmCount] = kmUsed;
                                kmLen[kmCount] = s.Length;
                                kmUsed += s.Length;
                                kmCount++;
                            }
                        }
                    }
                    else { r.Read(); r.Skip(); }
                }
            }
            else if (r.ValueTextEquals("merchant"))
            {
                r.Read();
                while (r.Read() && r.TokenType == JsonTokenType.PropertyName)
                {
                    if (r.ValueTextEquals("id"))
                    {
                        r.Read();
                        var id = r.ValueSpan;
                        bool known = false;
                        if (!r.HasValueSequence)
                        {
                            for (int k = 0; k < kmCount; k++)
                            {
                                if (kmLen[k] == id.Length &&
                                    id.SequenceEqual(kmBuf.Slice(kmStart[k], kmLen[k])))
                                { known = true; break; }
                            }
                        }
                        unknownMerchant = !known;
                    }
                    else if (r.ValueTextEquals("mcc"))
                    {
                        r.Read();
                        var mcc = r.ValueSpan;
                        mccRisk = DefaultMccRisk;
                        if (!r.HasValueSequence)
                        {
                            for (int k = 0; k < _mccKeys.Length; k++)
                                if (mcc.SequenceEqual(_mccKeys[k])) { mccRisk = _mccVals[k]; break; }
                        }
                    }
                    else if (r.ValueTextEquals("avg_amount")) { r.Read(); merchAvg = r.GetDouble(); }
                    else { r.Read(); r.Skip(); }
                }
            }
            else if (r.ValueTextEquals("terminal"))
            {
                r.Read();
                while (r.Read() && r.TokenType == JsonTokenType.PropertyName)
                {
                    if (r.ValueTextEquals("is_online")) { r.Read(); isOnline = r.GetBoolean(); }
                    else if (r.ValueTextEquals("card_present")) { r.Read(); cardPresent = r.GetBoolean(); }
                    else if (r.ValueTextEquals("km_from_home")) { r.Read(); kmHome = r.GetDouble(); }
                    else { r.Read(); r.Skip(); }
                }
            }
            else if (r.ValueTextEquals("last_transaction"))
            {
                r.Read();
                if (r.TokenType == JsonTokenType.StartObject)
                {
                    hasLast = true;
                    while (r.Read() && r.TokenType == JsonTokenType.PropertyName)
                    {
                        if (r.ValueTextEquals("timestamp")) { r.Read(); lastTs = r.GetDateTimeOffset(); }
                        else if (r.ValueTextEquals("km_from_current")) { r.Read(); lastKm = r.GetDouble(); }
                        else { r.Read(); r.Skip(); }
                    }
                }
                // else: Null token -> hasLast stays false
            }
            else
            {
                r.Read();
                r.Skip();
            }
        }

        DateTime utc = reqAt.UtcDateTime;
        v[0] = Clamp(amount / _n.max_amount);
        v[1] = Clamp(installments / _n.max_installments);
        v[2] = Clamp((amount / custAvg) / _n.amount_vs_avg_ratio);
        v[3] = (float)(utc.Hour / 23.0);
        v[4] = (float)((((int)utc.DayOfWeek + 6) % 7) / 6.0);
        if (hasLast)
        {
            v[5] = Clamp((reqAt - lastTs).TotalMinutes / _n.max_minutes);
            v[6] = Clamp(lastKm / _n.max_km);
        }
        else { v[5] = -1f; v[6] = -1f; }
        v[7] = Clamp(kmHome / _n.max_km);
        v[8] = Clamp(tx24 / _n.max_tx_count_24h);
        v[9] = isOnline ? 1f : 0f;
        v[10] = cardPresent ? 1f : 0f;
        v[11] = unknownMerchant ? 1f : 0f;
        v[12] = mccRisk;
        v[13] = Clamp(merchAvg / _n.max_merchant_avg_amount);
        return true;
    }
}
