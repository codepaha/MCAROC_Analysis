namespace MCAROC_Analysis.Data.Entities;

/// <summary>Paid model calls started for one purpose on one IST day: the ledger a daily cap is reserved against. A call is counted when it is
/// reserved, just before it is made, whatever its outcome; work answered without a call (a reused reading, a deferral) reserves nothing.</summary>
public class AiDailyCallCount
{
    public string Purpose { get; set; } = string.Empty;

    /// <summary>The IST calendar day.</summary>
    public DateOnly Day { get; set; }

    public int Calls { get; set; }
}
