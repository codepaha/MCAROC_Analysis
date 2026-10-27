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
        ChargeLitigationSignal.ChargedProject => "Project in dispute",
        _ => "Lender dispute"
    };

    /// <summary>Badge on a case (case list, case card, case page).</summary>
    public static string CaseBadge(ChargeLitigationSignal s) => s switch
    {
        ChargeLitigationSignal.ImmovableAddress => "Touches charged property",
        ChargeLitigationSignal.MovableIdentifier => "Names a charged asset",
        ChargeLitigationSignal.ChargedProject => "Unit in a charged project",
        _ => "Charge holder's recovery case"
    };

    /// <summary>Heading of a link in the charge drawer.</summary>
    public static string Heading(ChargeLitigationSignal s) => s switch
    {
        ChargeLitigationSignal.ImmovableAddress => "Case names the charged property",
        ChargeLitigationSignal.MovableIdentifier => "Case names an asset under this charge",
        ChargeLitigationSignal.ChargedProject => "Case names a unit in the charged project",
        _ => "Lender is litigating (indirect)"
    };

    /// <summary>One-line wording for lists (banner, PDF, CSV).</summary>
    public static string Phrase(ChargeLitigationSignal s) => s switch
    {
        ChargeLitigationSignal.ImmovableAddress => "order names the charged property",
        ChargeLitigationSignal.MovableIdentifier => "order names an asset under the charge",
        ChargeLitigationSignal.ChargedProject => "order names a unit in the charged project",
        _ => "lender is litigating (indirect)"
    };

    /// <summary>True for the signals where a court order itself names the charged asset (not the indirect lender signal, and not another unit
    /// of a charged project).</summary>
    public static bool NamesTheAsset(ChargeLitigationSignal s) => s is ChargeLitigationSignal.ImmovableAddress or ChargeLitigationSignal.MovableIdentifier;

    /// <summary>The severity class used by <c>mca-badge</c> / <c>mca-finding</c>.</summary>
    public static string Severity(ChargeLitigationSignal s) => NamesTheAsset(s) ? "sev-critical" : "sev-watch";
}
