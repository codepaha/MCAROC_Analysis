using System.Text.Json;
using System.Text.RegularExpressions;
using MCAROC_Analysis.Data.Entities;

namespace MCAROC_Analysis.Services.Analysis.Rules;

/// <summary>Flags open charges whose property particulars describe one of the company's own addresses —
/// the registered office, the business address, or an EPFO establishment (issue #191, the part that needs no
/// order/judgment text). Answers "is the company mortgaging its own premises?" from data already stored.
///
/// The three addresses are matched together as one pool, and each charge is compared with AddressMatcher,
/// which only reports a match on a shared plot number plus supporting locality/PIN evidence. A charge that
/// matches nothing is not reported: address formats differ too much for a non-match to mean "someone else's
/// property".</summary>
public static partial class ChargedPropertyAddressRules
{
    public const string ChargedPropertyIsOwnPremisesCode = "CHARGED_PROPERTY_IS_OWN_PREMISES";

    public const string RegisteredOfficeSource = "Registered office";
    public const string BusinessAddressSource = "Business address";
    public const string EpfoEstablishmentSource = "EPFO establishment";

    private sealed record OwnAddress(string Source, string Text, AddressFingerprint Fingerprint);

    private sealed record ChargeMatch(RocCharge Charge, RocChargeEvent Event, List<(OwnAddress Address, AddressMatch Match)> Addresses);

    public static List<RuleEvaluationOutcome> Evaluate(AnalysisContext ctx) => [EvaluateChargedPropertyVsOwnAddresses(ctx)];

    private static RuleEvaluationOutcome EvaluateChargedPropertyVsOwnAddresses(AnalysisContext ctx)
    {
        if (ctx.Charges.Count == 0)
            return RuleEvaluationOutcome.NotEvaluated(ChargedPropertyIsOwnPremisesCode, "No charge records available.");

        var ownAddresses = BuildOwnAddressPool(ctx);
        if (ownAddresses.Count == 0)
            return RuleEvaluationOutcome.NotEvaluated(ChargedPropertyIsOwnPremisesCode, "No registered, business or EPFO address on file.");

        // Same reasoning as EntityCrossReferenceRules: a satisfied charge is historical, and "is mortgaging"
        // must not be said about a charge repaid years ago.
        var openCharges = ctx.Charges.Where(c => c.SatisfactionDate is null).ToList();
        var chargesWithParticulars = openCharges
            .Where(c => c.Events.Any(e => !string.IsNullOrWhiteSpace(e.PropertyParticulars)))
            .ToList();
        if (chargesWithParticulars.Count == 0)
            return RuleEvaluationOutcome.NotEvaluated(ChargedPropertyIsOwnPremisesCode, "No open charge has property particulars.");

        var matches = new List<ChargeMatch>();
        foreach (var charge in chargesWithParticulars.OrderBy(c => c.ChargeId))
        {
            // Newest description first: a modification can restate or replace the security.
            var events = charge.Events
                .Where(e => !string.IsNullOrWhiteSpace(e.PropertyParticulars) && DescribesImmovableProperty(e))
                .OrderByDescending(e => e.EventDate ?? DateOnly.MinValue)
                .ThenByDescending(e => e.ChargeEventId);

            foreach (var ev in events)
            {
                var fingerprint = AddressMatcher.Fingerprint(ev.PropertyParticulars);
                var hits = ownAddresses
                    .Select(a => (Address: a, Match: AddressMatcher.Match(a.Fingerprint, fingerprint)))
                    .Where(h => h.Match is not null)
                    .Select(h => (h.Address, h.Match!))
                    .ToList();

                if (hits.Count > 0)
                {
                    matches.Add(new ChargeMatch(charge, ev, hits));
                    break; // one matching description is enough for this charge
                }
            }
        }

        if (matches.Count == 0)
            return RuleEvaluationOutcome.NotTriggered();

        var sources = matches.SelectMany(m => m.Addresses.Select(a => a.Address.Source)).Distinct().ToList();
        var sourceText = JoinWithAnd(sources.Select(DescribeSource).ToList());

        string summary;
        if (matches.Count == 1)
        {
            var m = matches[0];
            var first = m.Addresses[0].Match;
            summary = $"Open charge {m.Charge.RocChargeNumber} held by {HolderName(m.Charge)} is secured on property whose " +
                      $"description matches the company's own {sourceText} (plot {string.Join(", ", first.SharedPlotNumbers)}, " +
                      $"{TitleCase(first.SharedLocalityTerms)}).";
        }
        else
        {
            summary = $"{matches.Count} open charges are secured on property whose description matches the company's own " +
                      $"{sourceText}.";
        }

        return RuleEvaluationOutcome.Triggered(new FindingDraft(
            FindingSection.Charges, FindingSeverity.Watch, TemporalStatus.Current,
            ChargedPropertyIsOwnPremisesCode, "Charged Property Matches the Company's Own Premises",
            summary,
            MetricsJson: JsonSerializer.Serialize(new
            {
                matchCount = matches.Count,
                addressSources = sources,
                matches = matches.Select(m => new
                {
                    chargeNumber = m.Charge.RocChargeNumber,
                    chargeHolder = HolderName(m.Charge),
                    eventDate = m.Event.EventDate,
                    propertyParticulars = m.Event.PropertyParticulars,
                    addresses = m.Addresses.Select(a => new
                    {
                        source = a.Address.Source,
                        address = a.Address.Text,
                        matchBasis = a.Match.Basis.ToString(),
                        sharedPinCodes = a.Match.SharedPinCodes,
                        sharedPlotNumbers = a.Match.SharedPlotNumbers,
                        sharedLocalityTerms = a.Match.SharedLocalityTerms
                    })
                })
            }),
            SourceReferenceJson: JsonSerializer.Serialize(new
            {
                entityType = nameof(RocCharge),
                entityIds = matches.Select(m => m.Charge.ChargeId).OrderBy(id => id).ToArray()
            })));
    }

    /// <summary>The registered office, the business address when it is a different place, and each EPFO
    /// establishment address — one pool, so a charge is reported once however many of them it matches.</summary>
    private static List<OwnAddress> BuildOwnAddressPool(AnalysisContext ctx)
    {
        var pool = new List<OwnAddress>();
        var profile = ctx.CompanyProfile;

        if (profile is not null)
        {
            if (!string.IsNullOrWhiteSpace(profile.RegisteredAddress))
                pool.Add(new OwnAddress(RegisteredOfficeSource, profile.RegisteredAddress,
                    AddressMatcher.Fingerprint(profile.RegisteredAddress, profile.RegisteredAddressPinCode)));

            if (!string.IsNullOrWhiteSpace(profile.BusinessAddress) && !SameText(profile.BusinessAddress, profile.RegisteredAddress))
                pool.Add(new OwnAddress(BusinessAddressSource, profile.BusinessAddress, AddressMatcher.Fingerprint(profile.BusinessAddress)));
        }

        foreach (var epfo in ctx.EpfoEstablishments.Where(e => !string.IsNullOrWhiteSpace(e.Address)).OrderBy(e => e.EpfoEstablishmentId))
        {
            if (pool.Any(p => SameText(p.Text, epfo.Address))) continue;
            pool.Add(new OwnAddress(EpfoEstablishmentSource, epfo.Address!, AddressMatcher.Fingerprint(epfo.Address)));
        }

        // An address with no plot number can never meet AddressMatcher's bar, so it is not a usable source.
        return pool.Where(p => p.Fingerprint.PlotNumbers.Count > 0).ToList();
    }

    /// <summary>PropertyType names "Immovable property" for real-estate security; a "mortgage" in the
    /// particulars is taken as the same signal when PropertyType is missing. A charge on stock or book debts
    /// kept at the registered office is not a charge on the office, so those are left out.</summary>
    private static bool DescribesImmovableProperty(RocChargeEvent ev) =>
        (ev.PropertyType?.Contains("immovable", StringComparison.OrdinalIgnoreCase) ?? false)
        || MortgageRegex().IsMatch(ev.PropertyParticulars ?? string.Empty);

    [GeneratedRegex(@"\bmortgage", RegexOptions.IgnoreCase)]
    private static partial Regex MortgageRegex();

    private static string DescribeSource(string source) => source switch
    {
        RegisteredOfficeSource => "registered office address",
        BusinessAddressSource => "business address",
        EpfoEstablishmentSource => "EPFO establishment address",
        _ => source
    };

    private static string HolderName(RocCharge charge) =>
        string.IsNullOrWhiteSpace(charge.LatestChargeHolderRaw) ? "an unnamed holder" : charge.LatestChargeHolderRaw;

    private static bool SameText(string? a, string? b) =>
        a is not null && b is not null
        && string.Equals(Collapse(a), Collapse(b), StringComparison.OrdinalIgnoreCase);

    private static string Collapse(string s) => string.Join(' ', s.Split([' ', ',', '.'], StringSplitOptions.RemoveEmptyEntries));

    private static string TitleCase(IEnumerable<string> words) =>
        string.Join(", ", words.Select(w => w.Length <= 1 ? w : char.ToUpperInvariant(w[0]) + w[1..].ToLowerInvariant()));

    private static string JoinWithAnd(IReadOnlyList<string> items) => items.Count switch
    {
        0 => string.Empty,
        1 => items[0],
        2 => $"{items[0]} and {items[1]}",
        _ => $"{string.Join(", ", items.Take(items.Count - 1))} and {items[^1]}"
    };
}
