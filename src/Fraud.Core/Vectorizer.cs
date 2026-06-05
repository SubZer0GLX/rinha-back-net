using System.Runtime.CompilerServices;

namespace Fraud.Core;

/// <summary>Normalization constants (normalization.json).</summary>
public sealed class NormConstants
{
    public double max_amount { get; set; } = 10000;
    public double max_installments { get; set; } = 12;
    public double amount_vs_avg_ratio { get; set; } = 10;
    public double max_minutes { get; set; } = 1440;
    public double max_km { get; set; } = 1000;
    public double max_tx_count_24h { get; set; } = 20;
    public double max_merchant_avg_amount { get; set; } = 10000;
}

/// <summary>
/// Turns a transaction payload into the 14-dimension fraud vector, following the
/// exact normalization rules in REGRAS_DE_DETECCAO.md.
/// </summary>
public sealed class Vectorizer
{
    private readonly NormConstants _n;
    private readonly IReadOnlyDictionary<string, double> _mccRisk;
    private const double DefaultMccRisk = 0.5;

    public Vectorizer(NormConstants norm, IReadOnlyDictionary<string, double> mccRisk)
    {
        _n = norm;
        _mccRisk = mccRisk;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Clamp(double x)
    {
        if (double.IsNaN(x)) return 0f;
        if (x < 0.0) return 0f;
        if (x > 1.0) return 1f;
        return (float)x;
    }

    public void Compute(FraudRequest req, Span<float> v)
    {
        var t = req.transaction;
        var c = req.customer;
        var m = req.merchant;
        var term = req.terminal;

        // 0: amount
        v[0] = Clamp(t.amount / _n.max_amount);
        // 1: installments
        v[1] = Clamp(t.installments / _n.max_installments);
        // 2: amount_vs_avg
        v[2] = Clamp((t.amount / c.avg_amount) / _n.amount_vs_avg_ratio);

        // 3/4: hour of day & day of week (UTC). Mon=0 .. Sun=6.
        DateTime utc = t.requested_at.UtcDateTime;
        v[3] = (float)(utc.Hour / 23.0);
        int dow = ((int)utc.DayOfWeek + 6) % 7; // Sunday(0)->6, Monday(1)->0
        v[4] = (float)(dow / 6.0);

        // 5/6: minutes & km since last transaction, or -1 sentinel when none.
        if (req.last_transaction is { } last)
        {
            double minutes = (t.requested_at - last.timestamp).TotalMinutes;
            v[5] = Clamp(minutes / _n.max_minutes);
            v[6] = Clamp(last.km_from_current / _n.max_km);
        }
        else
        {
            v[5] = -1f;
            v[6] = -1f;
        }

        // 7: km_from_home
        v[7] = Clamp(term.km_from_home / _n.max_km);
        // 8: tx_count_24h
        v[8] = Clamp(c.tx_count_24h / _n.max_tx_count_24h);
        // 9: is_online
        v[9] = term.is_online ? 1f : 0f;
        // 10: card_present
        v[10] = term.card_present ? 1f : 0f;
        // 11: unknown_merchant (1 = unknown)
        v[11] = IsKnown(m.id, c.known_merchants) ? 0f : 1f;
        // 12: mcc_risk
        v[12] = (float)(_mccRisk.TryGetValue(m.mcc, out double risk) ? risk : DefaultMccRisk);
        // 13: merchant_avg_amount
        v[13] = Clamp(m.avg_amount / _n.max_merchant_avg_amount);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsKnown(string id, string[] known)
    {
        for (int i = 0; i < known.Length; i++)
            if (string.Equals(known[i], id, StringComparison.Ordinal)) return true;
        return false;
    }
}
