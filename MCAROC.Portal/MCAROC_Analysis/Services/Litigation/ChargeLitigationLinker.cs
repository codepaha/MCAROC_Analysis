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
    /// <summary>A court order's text names the charged immovable property: a plot/survey/door number plus a
    /// PIN code or locality (<see cref="AddressMatchStrength.Strong"/>).</summary>
    ImmovableAddress,

    /// <summary>The charge's holder (the lender) is a party in a recovery-type proceeding against the company.
    /// Indirect: it says the lender is litigating, not that this particular asset is.</summary>
    LenderRecoveryCase,

    /// <summary>A specific movable asset named in the charge (vehicle registration, engine/chassis or serial
    /// number) is named in a court order. Only an exact, whole-token identifier match counts.</summary>
    MovableIdentifier
}

/// <summary>The case half of a link. <see cref="Source"/> is "Court records" (data-lake) or "MCA workbook".</summary>
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

    public static ChargeLitigationSummary Build(
        IReadOnlyList<RocCharge> openCharges,
        IReadOnlyList<LitigationCase> cases,
        IReadOnlyDictionary<long, LitigationOrderDocument> docsByOrderId,
        IReadOnlyList<Litigation> workbookLitigations)
    {
        var links = new List<ChargeLitigationLink>();
        if (openCharges.Count == 0)
            return new ChargeLitigationSummary([], cases.Count, 0, 0, cases.Any(LitigationOrderAddressMatcher.IsNcltCase));

        links.AddRange(ImmovableAddressLinks(openCharges, cases, docsByOrderId));
        links.AddRange(LenderRecoveryLinks(openCharges, cases, workbookLitigations));
        var identifiersByCharge = IdentifiersByCharge(openCharges);
        links.AddRange(MovableIdentifierLinks(openCharges, identifiersByCharge, cases, docsByOrderId));

        var orders = cases.Where(c => !LitigationOrderAddressMatcher.IsNcltCase(c)).SelectMany(c => c.Orders).ToList();
        var withText = orders.Count(o => docsByOrderId.TryGetValue(o.LitigationCaseOrderId, out var d)
            && !string.IsNullOrWhiteSpace(d.ExtractedText) && d.TextExtractionStatus == FilingDocumentProcessingStatus.TextExtracted);

        return new ChargeLitigationSummary(
            links.OrderBy(l => l.ChargeNumber, StringComparer.OrdinalIgnoreCase).ThenBy(l => l.Signal).ToList(),
            CasesScanned: cases.Count + workbookLitigations.Count,
            OrdersScanned: withText,
            OrdersWithoutText: orders.Count - withText,
            NcltOrdersSkipped: cases.Any(LitigationOrderAddressMatcher.IsNcltCase),
            ChargesWithIdentifiers: identifiersByCharge.Count,
            MovableChargesWithoutIdentifiers: openCharges.Count(c => IsMovableCharge(c) && !identifiersByCharge.ContainsKey(c.ChargeId)));
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

    // ── Signal: charged immovable property named in an order ──────────────────────────────────────────────
    private static IEnumerable<ChargeLitigationLink> ImmovableAddressLinks(
        IReadOnlyList<RocCharge> charges, IReadOnlyList<LitigationCase> cases, IReadOnlyDictionary<long, LitigationOrderDocument> docs)
    {
        // Charged property only: no company premises, which the case card already reports separately.
        var pool = LitigationOrderAddressMatcher.BuildAddressPool(null, null, WithoutMovableIdentifiers(charges));
        if (pool.Count == 0 || cases.Count == 0) yield break;

        var caseByOrder = new Dictionary<long, LitigationCase>();
        foreach (var c in cases) foreach (var o in c.Orders) caseByOrder[o.LitigationCaseOrderId] = c;

        var strong = LitigationOrderAddressMatcher.MatchCasesStrongOnly(pool, cases, docs)
            .Where(m => m.RocChargeId is not null && caseByOrder.ContainsKey(m.LitigationCaseOrderId))
            .GroupBy(m => (ChargeId: m.RocChargeId!.Value, Case: caseByOrder[m.LitigationCaseOrderId].LitigationCaseId));

        foreach (var g in strong)
        {
            var first = g.OrderBy(m => m.PageNumber).First();
            var c = caseByOrder[first.LitigationCaseOrderId];
            var detail = string.Join(", ", first.MatchedPlotNumbers.Concat(first.MatchedLocalities).Append(first.MatchedPinCode ?? "").Where(s => !string.IsNullOrWhiteSpace(s)));
            var more = g.Count() > 1 ? $" (also on {g.Count() - 1} more page(s)/order(s))" : "";
            yield return new ChargeLitigationLink(
                first.RocChargeId!.Value, first.RocChargeNumber, first.ChargeHolder, ChargeLitigationSignal.ImmovableAddress,
                ToLinked(c),
                $"The order{(string.IsNullOrWhiteSpace(first.OrderDate) ? "" : $" dated {first.OrderDate}")} names the property charged under {first.RocChargeNumber} ({detail}){more}.",
                first.LitigationCaseOrderId, first.PageNumber, first.Excerpt,
                $"{first.OrderType ?? "Order"} {first.OrderDate}".Trim());
        }
    }

    /// <summary>A charge can name a vehicle and a plot in one clause. The address matcher counts any token with a
    /// digit as a plot/survey number, so a registration such as MH12AB1234 would make an order that merely mentions
    /// the vehicle look like a property match. The identifiers are blanked out of the text the address matcher sees
    /// (they are matched separately, as their own signal); only the fields it reads are copied.</summary>
    private static List<RocCharge> WithoutMovableIdentifiers(IReadOnlyList<RocCharge> charges)
    {
        var masked = new List<RocCharge>(charges.Count);
        foreach (var c in charges)
        {
            var ids = MovableIdentifierExtractor.Extract(c.Events.SelectMany(e => new[] { e.PropertyParticulars, e.ExtentAndOperation, e.InstrumentDescription, e.OtherTerms }).ToArray());
            masked.Add(new RocCharge
            {
                ChargeId = c.ChargeId, RocChargeNumber = c.RocChargeNumber, LatestChargeHolderRaw = c.LatestChargeHolderRaw,
                SatisfactionDate = c.SatisfactionDate,
                Events = c.Events.Select(e => new RocChargeEvent
                {
                    ChargeEventId = e.ChargeEventId, PropertyType = e.PropertyType,
                    PropertyParticulars = ids.Count == 0 ? e.PropertyParticulars : MovableIdentifierExtractor.Mask(e.PropertyParticulars, ids)
                }).ToList()
            });
        }
        return masked;
    }

    // ── Signal: the lender is litigating a recovery-type case against the company ─────────────────────────
    private static IEnumerable<ChargeLitigationLink> LenderRecoveryLinks(
        IReadOnlyList<RocCharge> charges, IReadOnlyList<LitigationCase> cases, IReadOnlyList<Litigation> workbook)
    {
        var holders = charges
            .Select(c => (Charge: c, Norm: EntityCrossReferenceRules.NormalizeCompanyName(c.LatestChargeHolderRaw ?? "")))
            .Where(h => h.Norm.Length >= 4) // too short/generic to trust a substring match (same floor as LITIGATION_BY_EXISTING_CHARGE_HOLDER)
            .ToList();
        if (holders.Count == 0) yield break;

        foreach (var c in cases)
        {
            var forum = RecoveryForum(c.Act, c.ProceedingType, c.CaseType, c.CaseClassification, c.Type, c.Court, c.CourtCategory, c.Bench);
            if (forum is null) continue;
            var parties = Parties(c.PetitionersJson, c.RespondentsJson);
            foreach (var (charge, norm) in holders)
                if (parties.Any(p => p.Contains(norm, StringComparison.Ordinal)))
                    yield return LenderLink(charge, ToLinked(c) with { Forum = forum });
        }

        foreach (var l in workbook.Where(l => l.MatchStatus == LitigationMatchStatus.Confirmed))
        {
            var forum = DossierComputations.IsDrt(l) ? "Debts Recovery Tribunal"
                : RecoveryForum(l.CaseType, l.CaseCategory, l.Court);
            if (forum is null || string.IsNullOrWhiteSpace(l.Litigants)) continue;
            var normLitigants = EntityCrossReferenceRules.NormalizeCompanyName(l.Litigants);
            foreach (var (charge, norm) in holders)
                if (normLitigants.Contains(norm, StringComparison.Ordinal))
                    yield return LenderLink(charge, new LinkedLitigation("MCA workbook", null, l.LitigationId, l.CaseNumber, l.Court, l.CaseStatus, l.Litigants, forum));
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
