namespace Fraud.Core;

// Request payload for POST /fraud-score. Plain classes so the API can wire up
// System.Text.Json source generation against them.

public sealed class FraudRequest
{
    public string? id { get; set; }
    public TransactionInfo transaction { get; set; } = new();
    public CustomerInfo customer { get; set; } = new();
    public MerchantInfo merchant { get; set; } = new();
    public TerminalInfo terminal { get; set; } = new();
    public LastTransactionInfo? last_transaction { get; set; }
}

public sealed class TransactionInfo
{
    public double amount { get; set; }
    public int installments { get; set; }
    public DateTimeOffset requested_at { get; set; }
}

public sealed class CustomerInfo
{
    public double avg_amount { get; set; }
    public int tx_count_24h { get; set; }
    public string[] known_merchants { get; set; } = Array.Empty<string>();
}

public sealed class MerchantInfo
{
    public string id { get; set; } = "";
    public string mcc { get; set; } = "";
    public double avg_amount { get; set; }
}

public sealed class TerminalInfo
{
    public bool is_online { get; set; }
    public bool card_present { get; set; }
    public double km_from_home { get; set; }
}

public sealed class LastTransactionInfo
{
    public DateTimeOffset timestamp { get; set; }
    public double km_from_current { get; set; }
}

public sealed class FraudResponse
{
    public bool approved { get; set; }
    public double fraud_score { get; set; }
}
