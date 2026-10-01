namespace MCAROC_Analysis.Services.LitigationData;

/// <summary>The one place the wording and badge styling for each link signal is defined, so the Charges tab, the
/// charge drawer, the Litigation tab and the PDF never describe the same link differently.</summary>
public static class ChargeLitigationLabels
{
    /// <summary>Short badge text (Charges tab).</summary>
    public static string Badge(ChargeLitigationSignal s) => s switch
    {
        ChargeLitigationSignal.ImmovableAddress => "Case found",
        ChargeLitigationSignal.MovableIdentifier => "Asset named in case",
        _ => "Lender dispute"
    };

    /// <summary>Heading of a link in the charge drawer.</summary>
    public static string Heading(ChargeLitigationSignal s) => s switch
    {
        ChargeLitigationSignal.ImmovableAddress => "Case names the charged property",
        ChargeLitigationSignal.MovableIdentifier => "Case names an asset under this charge",
        _ => "Lender is litigating (indirect)"
    };

    /// <summary>One-line wording for lists (banner, PDF, CSV).</summary>
    public static string Phrase(ChargeLitigationSignal s) => s switch
    {
        ChargeLitigationSignal.ImmovableAddress => "order names the charged property",
        ChargeLitigationSignal.MovableIdentifier => "order names an asset under the charge",
        _ => "lender is litigating (indirect)"
    };

    /// <summary>True for the two signals where a court order itself names the asset (as opposed to the indirect
    /// lender signal).</summary>
    public static bool NamesTheAsset(ChargeLitigationSignal s) => s != ChargeLitigationSignal.LenderRecoveryCase;

    /// <summary>The severity class used by <c>mca-badge</c> / <c>mca-finding</c>.</summary>
    public static string Severity(ChargeLitigationSignal s) => NamesTheAsset(s) ? "sev-critical" : "sev-watch";
}
