using MCAROC_Analysis.Data.Entities;

namespace MCAROC_Analysis.Models;

/// <summary>The words the request page, the dossier and the chat use for the entity (#539 PR 3). An LLP has an LLPIN, partners' contribution
/// and partners' funds where a company has a CIN, capital and equity, and designated partners where it has directors. One place, so every
/// surface says the same thing.</summary>
public sealed record EntityWording(bool IsLlp)
{
    public static readonly EntityWording Company = new(false);
    public static readonly EntityWording Llp = new(true);

    public static EntityWording For(EntityType? entityType) => entityType == EntityType.LLP ? Llp : Company;

    /// <summary>"CIN" / "LLPIN".</summary>
    public string Identifier => IsLlp ? "LLPIN" : "CIN";

    /// <summary>"company" / "LLP", in a sentence.</summary>
    public string Entity => IsLlp ? "LLP" : "company";

    /// <summary>The paid-in capital line: "Share Capital" / "Partners' contribution".</summary>
    public string Capital => IsLlp ? "Partners' contribution" : "Share Capital";

    /// <summary>The equity line: "Total Equity" / "Partners' funds".</summary>
    public string NetWorth => IsLlp ? "Partners' funds" : "Total Equity";

    /// <summary>"Directors" / "Designated partners".</summary>
    public string Directors => IsLlp ? "Designated partners" : "Directors";

    /// <summary>"director" / "designated partner", in a sentence.</summary>
    public string Director => IsLlp ? "designated partner" : "director";
}
