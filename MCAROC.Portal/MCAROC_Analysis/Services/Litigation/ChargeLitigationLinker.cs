using System.Text.Json;
using System.Text.RegularExpressions;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Models;
using MCAROC_Analysis.Services.Analysis;
using MCAROC_Analysis.Services.Analysis.Rules;

namespace MCAROC_Analysis.Services.LitigationData;

/// <summary>Why a case was tied to a charge. Only Strong evidence becomes a link (product decision), so each
/// signal defines its own "strong" bar.</summary>
public enum ChargeLitigationSignal
{
    /// <summary>A property a court order names is the charged immovable property itself (#554): read from the order and compared with the
    /// charged property field by field (<see cref="MCAROC_Analysis.Services.PropertyParticulars.OrderPropertyChargeMatcher"/>).</summary>
    ImmovableAddress,

    /// <summary>The charge's holder (the lender) is a party in a recovery-type proceeding against the company.
    /// Indirect: it says the lender is litigating, not that this particular asset is.</summary>
    LenderRecoveryCase,

    /// <summary>A specific movable asset named in the charge (vehicle registration, engine/chassis or serial
    /// number) is named in a court order. Only an exact, whole-token identifier match counts.</summary>
    MovableIdentifier,

    /// <summary>A court order names another unit or part of a project or building the charge secures (#554): a home buyer's flat in a
    /// project whose land is mortgaged. Not the charged asset itself, but the project under the charge is in dispute.</summary>
    ChargedProject
}

/// <summary>The case half of a link. <see cref="Source"/> is "Court records": the litigation service is the only source of cases.</summary>
public sealed record LinkedLitigation(
    string Source, long? LitigationCaseId, long? LitigationId, string? CaseNumber, string? Court, string? Status, string? Parties, string? Forum);

public sealed record ChargeLitigationLink(
    long ChargeId, string? ChargeNumber, string? ChargeHolder,
    ChargeLitigationSignal Signal, LinkedLitigation Case, string Explanation,
    long? OrderId = null, int? PageNumber = null, string? Excerpt = null, string? OrderLabel = null)
{
    /// <summary>Stable key for "the same case" across the two litigation sources.</summary>
    public string CaseKey => Case.LitigationCaseId is { } c ? $"lake:{c}" : $"wb:{Case.LitigationId}";
}

public sealed record ChargeLitigationSummary(
    IReadOnlyList<ChargeLitigationLink> Links, int CasesScanned, int OrdersScanned, int OrdersWithoutText, bool NcltOrdersSkipped,
    int ChargesWithIdentifiers = 0, int MovableChargesWithoutIdentifiers = 0,
    long? SnapshotId = null, string? Version = null)
{
    public static ChargeLitigationSummary Empty { get; } = new([], 0, 0, 0, false);

    public IReadOnlyList<ChargeLitigationLink> ForCharge(long chargeId) => Links.Where(l => l.ChargeId == chargeId).ToList();
    public bool HasAny => Links.Count > 0;
    public int ChargesWithLinks => Links.Select(l => l.ChargeId).Distinct().Count();
    public int LinkedCaseCount => Links.Select(l => l.CaseKey).Distinct().Count();
    /// <summary>Distinct cases in which a court order itself names the charged property or an asset under a charge.</summary>
    public int CasesNamingAssets => Links.Where(l => ChargeLitigationLabels.NamesTheAsset(l.Signal)).Select(l => l.CaseKey).Distinct().Count();
    public int CasesFor(ChargeLitigationSignal s) => Links.Where(l => l.Signal == s).Select(l => l.CaseKey).Distinct().Count();
    public IReadOnlySet<string> LinkedCaseKeys => Links.Select(l => l.CaseKey).ToHashSet();
}

/// <summary>Finds litigation that touches what the company's open charges secure. Pure and DB-free (the loader is
/// <see cref="ChargeLitigationService"/>), in the same style as <see cref="LitigationOrderAddressMatcher"/>, which
/// it builds on. Silence is not "clean": a charge whose text names no specific property or whose cases have no
/// extracted order text simply cannot be compared, which <see cref="ChargeLitigationSummary"/> reports.</summary>
public static partial class ChargeLitigationLinker
{
    private static readonly string[] RecoveryPhrases =
    [
        "SARFAESI", "SECURITISATION", "SECURITIZATION", "RECOVERY OF DEBTS", "DEBTS RECOVERY", "DEBT RECOVERY",
        "DEBTS RECOVERY TRIBUNAL", "RECOVERY SUIT", "SUIT FOR RECOVERY", "RECOVERY", "INSOLVENCY", "BANKRUPTCY"
    ];

    /// <param name="companyNames">The company's registered and former names: a lender's case counts only when the company is the respondent
    /// and the lender is on the other side. Without them no lender link is made (never a guess).</param>
    /// <param name="orderProperties">The properties each case's orders name, by case (#554); none when the orders have not been read.</param>
    /// <param name="charged">The immovable properties of <paramref name="openCharges"/> (<see cref="MCAROC_Analysis.Services.PropertyParticulars.OrderPropertyChargeMatcher.ChargedProperties"/>).</param>
    public static ChargeLitigationSummary Build(
        IReadOnlyList<RocCharge> openCharges,
        IReadOnlyList<LitigationCase> cases,
        IReadOnlyDictionary<long, LitigationOrderDocument> docsByOrderId,
        IReadOnlyCollection<string> companyNames,
        IReadOnlyDictionary<long, MCAROC_Analysis.Services.PropertyParticulars.CaseOrderProperties>? orderProperties = null,
        IReadOnlyList<MCAROC_Analysis.Services.PropertyParticulars.ChargedProperty>? charged = null)
    {
        var links = new List<ChargeLitigationLink>();
        if (openCharges.Count == 0)
            return new ChargeLitigationSummary([], cases.Count, 0, 0, cases.Any(LitigationOrderAddressMatcher.IsNcltCase));

        // Never from word matching (#554: any number plus any word made a "strong" match, 426 false links on Godrej, none real): the properties
        // each order names are read and compared with the charged properties field by field.
        links.AddRange(PropertyLinks(cases, orderProperties, charged, companyNames));
        links.AddRange(LenderRecoveryLinks(openCharges, cases, companyNames));
        var identifiersByCharge = IdentifiersByCharge(openCharges);
        links.AddRange(MovableIdentifierLinks(openCharges, identifiersByCharge, cases, docsByOrderId));

        var orders = cases.Where(c => !LitigationOrderAddressMatcher.IsNcltCase(c)).SelectMany(c => c.Orders).ToList();
        var withText = orders.Count(o => docsByOrderId.TryGetValue(o.LitigationCaseOrderId, out var d)
            && !string.IsNullOrWhiteSpace(d.ExtractedText) && d.TextExtractionStatus == FilingDocumentProcessingStatus.TextExtracted);

        return new ChargeLitigationSummary(
            links.OrderBy(l => l.ChargeNumber, StringComparer.OrdinalIgnoreCase).ThenBy(l => l.Signal).ToList(),
            CasesScanned: cases.Count,
            OrdersScanned: withText,
            OrdersWithoutText: orders.Count - withText,
            NcltOrdersSkipped: cases.Any(LitigationOrderAddressMatcher.IsNcltCase),
            ChargesWithIdentifiers: identifiersByCharge.Count,
            MovableChargesWithoutIdentifiers: openCharges.Count(c => IsMovableCharge(c) && !identifiersByCharge.ContainsKey(c.ChargeId)));
    }

    // ── Signals: the charged property, or a project under a charge, named in an order (#554) ───────────────
    private static IEnumerable<ChargeLitigationLink> PropertyLinks(IReadOnlyList<LitigationCase> cases,
        IReadOnlyDictionary<long, MCAROC_Analysis.Services.PropertyParticulars.CaseOrderProperties>? orderProperties,
        IReadOnlyList<MCAROC_Analysis.Services.PropertyParticulars.ChargedProperty>? charged, IReadOnlyCollection<string> companyNames)
    {
        if (orderProperties is null || charged is null || charged.Count == 0) yield break;
        foreach (var c in cases)
        {
            if (!orderProperties.TryGetValue(c.LitigationCaseId, out var props)) continue;
            var linked = new HashSet<(long ChargeId, ChargeLitigationSignal Signal)>();
            foreach (var p in props.Properties)
                foreach (var tie in MCAROC_Analysis.Services.PropertyParticulars.OrderPropertyChargeMatcher.Ties(p.Property, charged, companyNames))
                {
                    var signal = tie.Kind == MCAROC_Analysis.Services.PropertyParticulars.PropertyChargeTieKind.SameProperty
                        ? ChargeLitigationSignal.ImmovableAddress : ChargeLitigationSignal.ChargedProject;
                    if (!linked.Add((tie.Charge.ChargeId, signal))) continue;   // one link per charge, signal and case: the first order that names it
                    var m = p.Mentions[0];
                    var what = MCAROC_Analysis.Services.PropertyParticulars.OrderPropertyLabels.Role(p.Role).ToLowerInvariant();
                    var dated = string.IsNullOrWhiteSpace(m.OrderDate) ? "" : $" dated {m.OrderDate}";
                    var more = p.Mentions.Count > 1 ? $" (named in {p.Mentions.Count} orders)" : "";
                    var explanation = signal == ChargeLitigationSignal.ImmovableAddress
                        ? $"The order{dated} names {p.Description} ({what}), the property charged under {tie.Charge.ChargeNumber}{more}."
                        : $"The order{dated} names {p.Description} ({what}), in a project or building charged under {tie.Charge.ChargeNumber}: not the charged asset itself{more}.";
                    yield return new ChargeLitigationLink(tie.Charge.ChargeId, tie.Charge.ChargeNumber, tie.Charge.Holder, signal, ToLinked(c), explanation,
                        m.LitigationCaseOrderId, m.Page, m.Quote, $"{m.OrderType ?? "Order"} {m.OrderDate}".Trim());
                }
        }
    }

    // ── Signal: a specific movable named in the charge is named in an order ───────────────────────────────
    private static Dictionary<long, IReadOnlyList<MovableIdentifier>> IdentifiersByCharge(IReadOnlyList<RocCharge> charges)
    {
        var map = new Dictionary<long, IReadOnlyList<MovableIdentifier>>();
        foreach (var c in charges)
        {
            var texts = c.Events.SelectMany(e => new[] { e.PropertyParticulars, e.ExtentAndOperation, e.InstrumentDescription, e.OtherTerms }).ToArray();
            var ids = MovableIdentifierExtractor.Extract(texts);
            if (ids.Count > 0) map[c.ChargeId] = ids;
        }
        return map;
    }

    /// <summary>A charge over something other than land/buildings alone (stock, book debts, machinery, vehicles, deposits).</summary>
    private static bool IsMovableCharge(RocCharge c)
    {
        if (RequestDetailsViewModel.SecurityTypeLabels(c).Any(l => !string.Equals(l, nameof(SecurityType.ImmovableProperty), StringComparison.OrdinalIgnoreCase)))
            return true;
        return c.Events.Any(e => !string.IsNullOrWhiteSpace(e.PropertyType)
            && (e.PropertyType.Contains("book debt", StringComparison.OrdinalIgnoreCase)
                || (e.PropertyType.Contains("movable", StringComparison.OrdinalIgnoreCase) && !e.PropertyType.Contains("immovable", StringComparison.OrdinalIgnoreCase))));
    }

    private static IEnumerable<ChargeLitigationLink> MovableIdentifierLinks(
        IReadOnlyList<RocCharge> charges, IReadOnlyDictionary<long, IReadOnlyList<MovableIdentifier>> idsByCharge,
        IReadOnlyList<LitigationCase> cases, IReadOnlyDictionary<long, LitigationOrderDocument> docs)
    {
        if (idsByCharge.Count == 0 || cases.Count == 0) yield break;

        // One pass over every order page, looking for the identifiers of all charges at once.
        var chargeById = charges.ToDictionary(c => c.ChargeId);
        var all = idsByCharge.SelectMany(kv => kv.Value.Select(i => (ChargeId: kv.Key, Id: i))).ToList();
        var byNormalized = all.GroupBy(x => x.Id.Normalized).ToDictionary(g => g.Key, g => g.Select(x => x.ChargeId).Distinct().ToList());
        var distinctIds = all.Select(x => x.Id).GroupBy(i => i.Normalized).Select(g => g.First()).ToList();

        foreach (var c in cases)
        {
            if (LitigationOrderAddressMatcher.IsNcltCase(c)) continue;
            var perCharge = new Dictionary<long, List<(MovableIdentifierHit Hit, LitigationCaseOrder Order)>>();
            foreach (var order in c.Orders)
            {
                if (LitigationOrderAddressMatcher.IsNcltOrder(order)) continue;
                if (!docs.TryGetValue(order.LitigationCaseOrderId, out var doc)
                    || string.IsNullOrWhiteSpace(doc.ExtractedText) || doc.TextExtractionStatus != FilingDocumentProcessingStatus.TextExtracted) continue;

                foreach (var (pageNumber, pageText) in LitigationOrderAddressMatcher.PagesOf(doc.ExtractedText))
                    foreach (var hit in MovableIdentifierExtractor.FindInPage(distinctIds, pageNumber, pageText))
                        foreach (var chargeId in byNormalized[hit.Identifier.Normalized])
                        {
                            if (!perCharge.TryGetValue(chargeId, out var list)) perCharge[chargeId] = list = [];
                            list.Add((hit, order));
                        }
            }

            foreach (var (chargeId, hits) in perCharge)
            {
                var charge = chargeById[chargeId];
                var first = hits.OrderBy(h => h.Hit.PageNumber).First();
                var names = hits.Select(h => h.Hit.Identifier).GroupBy(i => i.Normalized).Select(g => g.First()).ToList();
                var what = string.Join(", ", names.Select(i => $"{i.KindLabel} {i.Raw}"));
                yield return new ChargeLitigationLink(
                    chargeId, charge.RocChargeNumber, charge.LatestChargeHolderRaw, ChargeLitigationSignal.MovableIdentifier, ToLinked(c),
                    $"The order{(string.IsNullOrWhiteSpace(first.Order.OrderDate) ? "" : $" dated {first.Order.OrderDate}")} names {what}, which is listed in the security for {charge.RocChargeNumber}.",
                    first.Order.LitigationCaseOrderId, first.Hit.PageNumber, first.Hit.Excerpt,
                    $"{first.Order.OrderType ?? "Order"} {first.Order.OrderDate}".Trim());
            }
        }
    }

    // ── Signal: the lender is litigating a recovery-type case against the company ─────────────────────────
    /// <summary>The charge's holder brings a recovery-type case against the company: the company is a respondent, and the holder is a petitioner
    /// (a borrower or guarantor suing the bank, with the company a co-respondent, is not the lender enforcing; nor is the lender's case against
    /// someone else).</summary>
    private static IEnumerable<ChargeLitigationLink> LenderRecoveryLinks(
        IReadOnlyList<RocCharge> charges, IReadOnlyList<LitigationCase> cases, IReadOnlyCollection<string> companyNames)
    {
        var companyCores = companyNames.Select(LitigationCompanySides.Core).Where(c => c.Length > 0).Distinct().ToList();
        if (companyCores.Count == 0) yield break;
        var holders = charges
            .Select(c => (Charge: c, Norm: EntityCrossReferenceRules.NormalizeCompanyName(c.LatestChargeHolderRaw ?? "")))
            .Where(h => h.Norm.Length >= 4) // too short/generic to trust a substring match (same floor as LITIGATION_BY_EXISTING_CHARGE_HOLDER)
            .ToList();
        if (holders.Count == 0) yield break;

        foreach (var c in cases)
        {
            var forum = RecoveryForum(c.Act, c.ProceedingType, c.CaseType, c.CaseClassification, c.Type, c.Court, c.CourtCategory, c.Bench);
            if (forum is null) continue;
            if (!RawParties(c.RespondentsJson).Any(r => LitigationCompanySides.IsCompany(r, companyCores))) continue;
            var petitioners = Parties(c.PetitionersJson);
            foreach (var (charge, norm) in holders)
                if (petitioners.Any(p => p.Contains(norm, StringComparison.Ordinal)))
                    yield return LenderLink(charge, ToLinked(c) with { Forum = forum });
        }
    }

    private static ChargeLitigationLink LenderLink(RocCharge charge, LinkedLitigation lit)
    {
        var assets = RequestDetailsViewModel.SecurityTypeLabels(charge);
        var over = assets.Count > 0 ? $" The charge covers {string.Join(", ", assets.Select(a => Regex.Replace(a, "(\\B[A-Z])", " $1").ToLowerInvariant()))}." : "";
        return new ChargeLitigationLink(
            charge.ChargeId, charge.RocChargeNumber, charge.LatestChargeHolderRaw, ChargeLitigationSignal.LenderRecoveryCase, lit,
            $"{charge.LatestChargeHolderRaw}, the holder of this charge, is a party in a recovery-type proceeding ({lit.Forum}) against the company. " +
            $"Indirect: the assets under this charge may be contested.{over}");
    }

    private static string? RecoveryForum(params string?[] fields)
    {
        var text = string.Join(" ", fields.Where(f => !string.IsNullOrWhiteSpace(f))).ToUpperInvariant();
        if (text.Length == 0) return null;
        if (TribunalWord().IsMatch(text)) return "Debts Recovery Tribunal";
        foreach (var phrase in RecoveryPhrases)
            if (text.Contains(phrase, StringComparison.Ordinal))
                return phrase is "INSOLVENCY" or "BANKRUPTCY" ? "Insolvency proceeding" : phrase is "SARFAESI" or "SECURITISATION" or "SECURITIZATION" ? "SARFAESI" : "Recovery proceeding";
        return null;
    }

    private static List<string> RawParties(string? partyJson)
    {
        if (string.IsNullOrWhiteSpace(partyJson)) return [];
        try { return LitigationPartyNames.Parse(partyJson); }
        catch (JsonException) { return []; }
    }

    private static List<string> Parties(params string?[] partyJsons)
    {
        var names = new List<string>();
        foreach (var json in partyJsons)
        {
            if (string.IsNullOrWhiteSpace(json)) continue;
            try
            {
                foreach (var n in LitigationPartyNames.Parse(json))
                    names.Add(EntityCrossReferenceRules.NormalizeCompanyName(n));
            }
            catch (JsonException) { /* unparseable party list: no claim, never a guess */ }
        }
        return names;
    }

    private static LinkedLitigation ToLinked(LitigationCase c)
    {
        string? parties = null;
        try
        {
            var sides = new[] { c.PetitionersJson, c.RespondentsJson }
                .Select(j => string.IsNullOrWhiteSpace(j) ? "" : string.Join(", ", LitigationPartyNames.Parse(j)))
                .Where(s => s.Length > 0).ToList();
            if (sides.Count > 0) parties = string.Join(" v. ", sides);
        }
        catch (JsonException) { }
        return new LinkedLitigation("Court records", c.LitigationCaseId, null, c.CaseNumber ?? c.Cnr, c.Court, c.CaseStatus, parties, null);
    }

    [GeneratedRegex(@"\b(DRT|DRAT)\b")]
    private static partial Regex TribunalWord();
}
