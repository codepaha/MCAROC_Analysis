using System.Text.Json;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Services.PropertyParticulars;

namespace MCAROC_Analysis.Services.Analysis.Rules;

public enum PremisesCategory
{
    RegisteredOffice,
    BusinessAddress,
    OperatingFacility,
    OtherCollateral
}

public sealed record CompanyPremisesAddress(
    PremisesCategory Category,
    string Label,
    string Text,
    IReadOnlyList<string?> PlaceNames);

public sealed record PremisesMatchResult(
    PremisesCategory Category,
    string Label,
    AddressMatchResult Result,
    CompanyPremisesAddress? MatchedPremises = null,
    int? MatchedItemIndex = null)
{
    public bool IsCompanyPremises => Result.Strength == AddressMatchStrength.Strong;

    public static PremisesMatchResult OtherCollateral(string? reason = null) =>
        new(PremisesCategory.OtherCollateral,
            reason ?? "No match to filed company premises",
            AddressMatchResult.NoMatch);
}

/// <summary>Flags when an open charge's mortgaged property appears to be the company's own premises — its
/// MCA-filed registered office, its business address, or an EPFO-registered establishment (the address-pool
/// half of issue #191, which needs no PDF extraction and no litigation-corpus fix). Mortgaging the company's
/// own operating premises is ordinary for a borrower, but it is a materially different exposure from a
/// third-party or promoter-owned collateral: enforcement lands on the place the business actually runs.
/// Only a <see cref="AddressMatchStrength.Strong"/> match raises the finding, and even then it is phrased as
/// "appears to", with the matched evidence in MetricsJson for the reviewer to confirm against the instrument.
/// Absence of a match is never stated as "the collateral is third-party property" — the address pool is only
/// the company's own filed addresses, so a non-match proves nothing.</summary>
public static class ChargedPropertyAddressRules
{
    public const string ChargedPropertyIsCompanyPremisesCode = "CHARGED_PROPERTY_IS_COMPANY_PREMISES";

    /// <summary>Used only when PropertyType is blank (≈25% of real Coastal events): a description that names
    /// land/buildings is the one worth comparing to a premises address. When PropertyType IS filed and omits
    /// "Immovable", a matching address is where hypothecated stock sits, not what is mortgaged — skipped.</summary>
    private static readonly string[] ImmovableKeywords =
        ["MORTGAGE", "LAND", "BUILDING", "PLOT", "FLAT", "PREMISES", "IMMOVABLE", "FACTORY", "SURVEY", "ARAZI", "GATA", "KHATA"];

    public static List<RuleEvaluationOutcome> Evaluate(AnalysisContext ctx) => [EvaluateChargedPropertyVsOwnAddresses(ctx)];

    private static RuleEvaluationOutcome EvaluateChargedPropertyVsOwnAddresses(AnalysisContext ctx)
    {
        if (ctx.Charges.Count == 0)
            return RuleEvaluationOutcome.NotEvaluated(ChargedPropertyIsCompanyPremisesCode, "No charge records available.");

        var pool = BuildAddressPool(ctx.CompanyProfile, ctx.EpfoEstablishments);
        if (pool.Count == 0)
            return RuleEvaluationOutcome.NotEvaluated(ChargedPropertyIsCompanyPremisesCode,
                "No registered, business, or EPFO establishment address on file to compare charged property against.");

        // Satisfied charges are historical, same as EntityCrossReferenceRules — a released mortgage is not
        // a current claim on the premises.
        var openEvents = ctx.Charges
            .Where(c => c.SatisfactionDate is null)
            .SelectMany(c => c.Events.Select(e => (Charge: c, Event: e)))
            .Where(x => !string.IsNullOrWhiteSpace(x.Event.PropertyParticulars))
            .ToList();
        if (openEvents.Count == 0)
            return RuleEvaluationOutcome.NotEvaluated(ChargedPropertyIsCompanyPremisesCode,
                "No open (unsatisfied) charge has property particulars on file.");

        var excludedPlaces = pool.SelectMany(p => p.PlaceNames).ToList();

        var matches = new List<PremisesMatch>();
        foreach (var (charge, ev) in openEvents)
        {
            if (!DescribesImmovableProperty(ev)) continue;

            var reading = PropertyReading.For(ev.PropertyParticulars, ev.PropertyType, ctx.PropertyExtractions);

            if (reading.Items.Count > 0)
            {
                for (var i = 0; i < reading.Items.Count; i++)
                {
                    var item = reading.Items[i];
                    if (!AddressMatcher.IsImmovable(item)) continue;

                    foreach (var address in pool)
                    {
                        var result = AddressMatcher.Match(address.Text, item, excludedPlaces);
                        if (result.Strength == AddressMatchStrength.Strong)
                        {
                            matches.Add(new PremisesMatch(charge, ev, address, result, i));
                        }
                    }
                }
            }

            else
            {
                foreach (var address in pool)
                {
                    var result = AddressMatcher.Match(address.Text, ev.PropertyParticulars, excludedPlaces);
                    if (result.Strength == AddressMatchStrength.Strong)
                        matches.Add(new PremisesMatch(charge, ev, address, result, null));
                }
            }
        }

        if (matches.Count == 0)
            return RuleEvaluationOutcome.NotTriggered();

        // One line per (charge, address) — a charge modified five times repeats the same particulars on
        // every event, and that must not read as five separate mortgages.
        var distinct = matches
            .GroupBy(m => (m.Charge.ChargeId, m.Address.Label))
            .Select(g => g.First())
            .OrderBy(m => m.Charge.ChargeId)
            .ToList();
        var charges = distinct.Select(m => m.Charge).DistinctBy(c => c.ChargeId).ToList();
        var labels = distinct.Select(m => m.Address.Label).Distinct().ToList();

        var premises = labels.Count == 1 ? labels[0].ToLowerInvariant() : "own premises";
        var summary = charges.Count == 1
            ? $"Open charge {charges[0].RocChargeNumber} ({charges[0].LatestChargeHolderRaw}) appears to secure the company's {premises} — the charged property description shares its plot/survey number and PIN code or locality. Confirm against the charge instrument."
            : $"{charges.Count} open charges (including {charges[0].RocChargeNumber}, {charges[0].LatestChargeHolderRaw}) appear to secure the company's {premises} — the charged property descriptions share plot/survey numbers and PIN codes or localities. Confirm against the charge instruments.";

        return RuleEvaluationOutcome.Triggered(new FindingDraft(
            FindingSection.Charges, FindingSeverity.Watch, TemporalStatus.Current,
            ChargedPropertyIsCompanyPremisesCode, "Charged Property Appears to Be the Company's Own Premises",
            summary,
            MetricsJson: JsonSerializer.Serialize(new
            {
                matchCount = distinct.Count,
                matches = distinct.Select(m => new
                {
                    chargeNumber = m.Charge.RocChargeNumber,
                    chargeHolder = m.Charge.LatestChargeHolderRaw,
                    chargeEventId = m.Event.ChargeEventId,
                    premisesCategory = m.Address.Category.ToString(),
                    addressSource = m.Address.Label,
                    address = m.Address.Text,
                    propertyItemIndex = m.ItemIndex,
                    matchedPlotNumbers = m.Result.MatchedPlotNumbers,
                    matchedPinCode = m.Result.MatchedPinCode,
                    matchedLocalities = m.Result.MatchedLocalities
                })
            }),
            // RocCharge ids so the finding also lists on each charge's drawer (ChargeDrawerViewModel).
            SourceReferenceJson: JsonSerializer.Serialize(new
            {
                entityType = nameof(RocCharge),
                entityIds = charges.Select(c => c.ChargeId).OrderBy(id => id).ToArray()
            })));
    }

    public static bool DescribesImmovableProperty(RocChargeEvent ev)
    {
        if (!string.IsNullOrWhiteSpace(ev.PropertyType))
            return ev.PropertyType.Contains("immovable", StringComparison.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(ev.PropertyParticulars))
            return false;

        var text = ev.PropertyParticulars.ToUpperInvariant();
        return ImmovableKeywords.Any(text.Contains);
    }

    /// <summary>The registered office, business address, and EPFO establishment addresses, matched together as
    /// one pool per request (issue #191). A business address identical to the registered one is dropped so a
    /// single match doesn't report twice.</summary>
    public static List<CompanyPremisesAddress> BuildAddressPool(CompanyProfile? profile, IEnumerable<EpfoEstablishment>? epfoEstablishments)
    {
        var pool = new List<CompanyPremisesAddress>();
        if (!string.IsNullOrWhiteSpace(profile?.RegisteredAddress))
            pool.Add(new CompanyPremisesAddress(PremisesCategory.RegisteredOffice, "Registered office", profile.RegisteredAddress,
                [profile.RegisteredAddressCity, profile.RegisteredAddressState]));

        if (!string.IsNullOrWhiteSpace(profile?.BusinessAddress)
            && !string.Equals(profile.BusinessAddress.Trim(), profile.RegisteredAddress?.Trim(), StringComparison.OrdinalIgnoreCase))
            pool.Add(new CompanyPremisesAddress(PremisesCategory.BusinessAddress, "Business address", profile.BusinessAddress, [profile.RegisteredAddressCity]));

        foreach (var est in (epfoEstablishments ?? []).Where(e => !string.IsNullOrWhiteSpace(e.Address)))
            pool.Add(new CompanyPremisesAddress(PremisesCategory.OperatingFacility, $"EPFO establishment {est.EstablishmentId}", est.Address!, [est.City]));

        return pool;
    }

    /// <summary>Classifies a single property reading item against the company's premises address pool.</summary>
    public static PremisesMatchResult ClassifyItem(
        PropertyReadingItem item,
        IEnumerable<CompanyPremisesAddress> addressPool,
        IEnumerable<string?>? excludedPlaces = null,
        int? itemIndex = null)
    {
        if (item is null) return PremisesMatchResult.OtherCollateral();
        var poolList = addressPool as IReadOnlyList<CompanyPremisesAddress> ?? addressPool.ToList();
        var excluded = (excludedPlaces ?? poolList.SelectMany(p => p.PlaceNames)).ToList();

        foreach (var address in poolList)
        {
            var result = AddressMatcher.Match(address.Text, item, excluded);
            if (result.Strength == AddressMatchStrength.Strong)
                return new PremisesMatchResult(address.Category, address.Label, result, address, itemIndex);
        }

        return PremisesMatchResult.OtherCollateral();
    }

    /// <summary>Classifies an entire charge (checking its property particulars events and items) against the company's
    /// premises address pool. Returns the strongest premises match or OtherCollateral if none match.</summary>
    public static PremisesMatchResult ClassifyCharge(
        RocCharge charge,
        IEnumerable<CompanyPremisesAddress> addressPool,
        IReadOnlyDictionary<string, PropertyParticularsExtraction>? extractions = null)
    {
        var poolList = addressPool as IReadOnlyList<CompanyPremisesAddress> ?? addressPool.ToList();
        if (poolList.Count == 0) return PremisesMatchResult.OtherCollateral();
        var excluded = poolList.SelectMany(p => p.PlaceNames).ToList();

        var events = charge.Events.OrderByDescending(e => e.EventDate ?? DateOnly.MinValue).ThenByDescending(e => e.ChargeEventId);
        foreach (var ev in events)
        {
            if (string.IsNullOrWhiteSpace(ev.PropertyParticulars)) continue;
            if (!DescribesImmovableProperty(ev)) continue;

            var reading = PropertyReading.For(ev.PropertyParticulars, ev.PropertyType, extractions);
            if (reading.Items.Count > 0)
            {
                for (var i = 0; i < reading.Items.Count; i++)
                {
                    var itemMatch = ClassifyItem(reading.Items[i], poolList, excluded, i);
                    if (itemMatch.IsCompanyPremises)
                        return itemMatch;
                }
            }
            else if (DescribesImmovableProperty(ev))
            {
                foreach (var address in poolList)
                {
                    var result = AddressMatcher.Match(address.Text, ev.PropertyParticulars, excluded);
                    if (result.Strength == AddressMatchStrength.Strong)
                        return new PremisesMatchResult(address.Category, address.Label, result, address, null);
                }
            }
        }

        return PremisesMatchResult.OtherCollateral();
    }

    private sealed record PremisesMatch(
        RocCharge Charge,
        RocChargeEvent Event,
        CompanyPremisesAddress Address,
        AddressMatchResult Result,
        int? ItemIndex = null);
}
