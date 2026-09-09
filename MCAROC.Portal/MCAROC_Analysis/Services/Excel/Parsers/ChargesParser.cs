using MCAROC_Analysis.Data.Entities;

namespace MCAROC_Analysis.Services.Excel.Parsers;

/// <summary>The highest-risk parser: reconstructs each charge's full lifecycle.
///
/// Base events always come from the ROC report's "Open/Satisfied Charges Sequence" sheets (9 columns:
/// SERIAL NUMBER, CHARGE ID, STATUS, DATE, FILING DATE, HOLDER NAME, CHARGE AMOUNT, PROPERTY TYPE,
/// NUMBER OF HOLDERS) — these alone give every event's date/holder/amount/status.
///
/// Enrichment (instrument description, rate of interest, terms, property particulars, etc.) comes from
/// two possible 18-column "detail" sheets sharing the same layout: the ROC report's own "Latest Event on
/// Open Charges" (always available, but only covers the single latest event per open charge) and, when
/// uploaded and identity-matched, the charge report's "Open/Satisfied Charges in Details" (covers every
/// event). Charge-report matches are applied after ROC-report matches so they take precedence on overlap.
///
/// Matching never trusts SERIAL NUMBER alone (a presentation artifact) — it uses a hierarchy of
/// (ChargeId, EventType, EventDate, Amount) down to (ChargeId, SerialNumber), recording MatchConfidence.</summary>
public static class ChargesParser
{
    private const string ParserName = nameof(ChargesParser);

    private record DetailRow(
        string ChargeId, string SerialNumber, ChargeEventType? EventType, DateOnly? EventDate, decimal? Amount,
        string? InstrumentDescription, string? RateOfInterest, string? TermsOfPayment, string? PropertyParticulars,
        string? ExtentAndOperation, string? OtherTerms, string? ModificationParticulars,
        bool? JointHolding, bool? ConsortiumHolding);

    private record ParsedEvent(string ChargeId, RocChargeEvent Event);

    public static ParseResult<RocCharge> Parse(
        IReadOnlyList<SheetData> rocWorkbook,
        IReadOnlyList<SheetData>? chargeWorkbook,
        bool chargeWorkbookIdentityMatches,
        long requestId, long ingestionRunId,
        long? rocSourceDocumentId, long? chargeSourceDocumentId)
    {
        var result = new ParseResult<RocCharge>();

        var openSeq = SheetAliases.Find(rocWorkbook, SheetAliases.OpenChargesSequence);
        var satisfiedSeq = SheetAliases.Find(rocWorkbook, SheetAliases.SatisfiedChargesSequence);

        var parsedEvents = new List<ParsedEvent>();
        if (openSeq is not null) parsedEvents.AddRange(ParseSequenceSheet(openSeq, requestId, ingestionRunId, rocSourceDocumentId, result));
        if (satisfiedSeq is not null) parsedEvents.AddRange(ParseSequenceSheet(satisfiedSeq, requestId, ingestionRunId, rocSourceDocumentId, result));

        if (parsedEvents.Count == 0)
            return result; // no charges at all — not an error, just nothing to report

        // Build enrichment candidate pool: ROC's own "Latest Event on Open Charges" first, then the charge
        // report's fuller detail sheets (if identity-matched) so they win on overlapping matches.
        var detailRows = new List<DetailRow>();

        var latestOpen = SheetAliases.Find(rocWorkbook, ["Latest Event on Open Charges"]);
        if (latestOpen is not null) detailRows.AddRange(ParseDetailSheet(latestOpen));

        if (chargeWorkbook is not null && chargeWorkbookIdentityMatches)
        {
            var openDetails = SheetAliases.Find(chargeWorkbook, SheetAliases.OpenChargesDetails);
            var satisfiedDetails = SheetAliases.Find(chargeWorkbook, SheetAliases.SatisfiedChargesDetails);
            if (openDetails is not null) detailRows.AddRange(ParseDetailSheet(openDetails));
            if (satisfiedDetails is not null) detailRows.AddRange(ParseDetailSheet(satisfiedDetails));
        }

        foreach (var pe in parsedEvents)
            MatchAndEnrich(pe.ChargeId, pe.Event, detailRows);

        foreach (var pe in parsedEvents)
            ClassifySecurity(pe.Event, requestId, ingestionRunId);

        var charges = parsedEvents
            .GroupBy(pe => pe.ChargeId)
            .Select(g => BuildChargeHeader(g.Key, g.Select(pe => pe.Event).ToList(), requestId, ingestionRunId, rocSourceDocumentId))
            .ToList();

        result.Items.AddRange(charges);
        return result;
    }

    private static IEnumerable<ParsedEvent> ParseSequenceSheet(
        SheetData sheet, long requestId, long ingestionRunId, long? sourceDocumentId, ParseResult<RocCharge> result)
    {
        for (var r = 1; r < sheet.Rows.Count; r++)
        {
            var row = sheet.Rows[r];
            var chargeId = ParseChargeId(Cell(row, 1));
            if (chargeId is null) continue;

            var serial = Cell(row, 0)?.ToString()?.Trim() ?? string.Empty;
            var statusText = Cell(row, 2)?.ToString()?.Trim();
            if (!Enum.TryParse<ChargeEventType>(statusText, true, out var eventType))
            {
                result.AddWarning(new ParseIssue(IssueSeverity.Warning, ParserName, "Status", statusText,
                    "UNKNOWN_EVENT_TYPE", $"Unrecognized charge event status '{statusText}' for charge {chargeId}", r + 1));
                continue;
            }

            DateNormalizer.TryParse(Cell(row, 3), out var eventDate);
            DateNormalizer.TryParse(Cell(row, 4), out var filingDate);
            AmountNormalizer.TryParse(Cell(row, 6), out var amount, out var amountRaw);
            var holderRaw = Cell(row, 5)?.ToString()?.Trim() ?? string.Empty;
            var propertyType = Cell(row, 7)?.ToString()?.Trim();
            AmountNormalizer.TryParse(Cell(row, 8), out var numHolders, out _);

            var ev = new RocChargeEvent
            {
                RequestId = requestId,
                IngestionRunId = ingestionRunId,
                SourceDocumentId = sourceDocumentId,
                SourceSheetName = sheet.Name,
                SourceRowNumber = r + 1,
                SerialNumber = serial,
                EventType = eventType,
                EventDate = eventDate,
                FilingDate = filingDate,
                ChargeAmount = amount,
                ChargeAmountRaw = amountRaw,
                HolderNameRaw = holderRaw,
                HolderNameNormalized = NameNormalizer.Normalize(holderRaw),
                PropertyType = propertyType is "-" ? null : propertyType,
                NumberOfHolders = numHolders is null ? null : (int)numHolders.Value,
                MatchConfidence = ChargeEventMatchConfidence.Unmatched
            };

            yield return new ParsedEvent(chargeId, ev);
        }
    }

    private static IEnumerable<DetailRow> ParseDetailSheet(SheetData sheet)
    {
        for (var r = 1; r < sheet.Rows.Count; r++)
        {
            var row = sheet.Rows[r];
            var chargeId = ParseChargeId(Cell(row, 1));
            if (chargeId is null) continue;

            var serial = Cell(row, 0)?.ToString()?.Trim() ?? string.Empty;
            var statusText = Cell(row, 2)?.ToString()?.Trim();
            Enum.TryParse<ChargeEventType>(statusText, true, out var eventType);
            DateNormalizer.TryParse(Cell(row, 3), out var eventDate);
            AmountNormalizer.TryParse(Cell(row, 6), out var amount, out _);

            yield return new DetailRow(
                chargeId, serial, statusText is null ? null : eventType, eventDate, amount,
                NullIfDash(Cell(row, 9)), NullIfDash(Cell(row, 10)), NullIfDash(Cell(row, 11)),
                NullIfDash(Cell(row, 12)), NullIfDash(Cell(row, 13)), NullIfDash(Cell(row, 14)),
                NullIfDash(Cell(row, 15)), ParseYesNo(Cell(row, 16)), ParseYesNo(Cell(row, 17)));
        }
    }

    private static void MatchAndEnrich(string chargeId, RocChargeEvent ev, List<DetailRow> details)
    {
        var candidates = details.Where(d => d.ChargeId == chargeId).ToList();
        if (candidates.Count == 0) return;

        var match = candidates.FirstOrDefault(d => d.EventType == ev.EventType && d.EventDate == ev.EventDate && d.Amount == ev.ChargeAmount);
        var confidence = ChargeEventMatchConfidence.Exact;
        var method = "ChargeId+EventType+EventDate+Amount";

        if (match is null)
        {
            match = candidates.FirstOrDefault(d => d.EventType == ev.EventType && d.EventDate == ev.EventDate);
            confidence = ChargeEventMatchConfidence.Partial;
            method = "ChargeId+EventType+EventDate";
        }

        if (match is null)
        {
            match = candidates.FirstOrDefault(d => d.EventType == ev.EventType && d.Amount == ev.ChargeAmount);
            confidence = ChargeEventMatchConfidence.Partial;
            method = "ChargeId+EventType+Amount";
        }

        if (match is null)
        {
            match = candidates.FirstOrDefault(d => d.SerialNumber == ev.SerialNumber);
            confidence = ChargeEventMatchConfidence.SerialNumberOnly;
            method = "ChargeId+SerialNumber";
        }

        if (match is null) return; // stays Unmatched — event keeps its base Sequence-sheet data only

        ev.InstrumentDescription = match.InstrumentDescription;
        ev.RateOfInterest = match.RateOfInterest;
        ev.TermsOfPayment = match.TermsOfPayment;
        ev.PropertyParticulars = match.PropertyParticulars;
        ev.ExtentAndOperation = match.ExtentAndOperation;
        ev.OtherTerms = match.OtherTerms;
        ev.ModificationParticulars = match.ModificationParticulars;
        ev.JointHolding = match.JointHolding;
        ev.ConsortiumHolding = match.ConsortiumHolding;
        ev.MatchConfidence = confidence;
        ev.MatchMethod = method;
    }

    /// <summary>Runs the deterministic security classifier over one event's enrichment narrative and
    /// attaches the normalized facility/security/ranking/arrangement + component rows. Raw text stays put.</summary>
    private static void ClassifySecurity(RocChargeEvent ev, long requestId, long ingestionRunId)
    {
        var c = ChargeSecurityClassifier.Classify(
            ev.InstrumentDescription, ev.PropertyType, ev.PropertyParticulars,
            ev.ExtentAndOperation, ev.OtherTerms, ev.JointHolding, ev.ConsortiumHolding);

        if (c.OverallConfidence == ChargeClassificationConfidence.None) return;

        ev.FacilityTypesJson = c.FacilityTypes.Count > 0
            ? System.Text.Json.JsonSerializer.Serialize(c.FacilityTypes.Select(f => f.ToString())) : null;
        ev.PrimaryFacilityType = c.PrimaryFacilityType;
        ev.Arrangement = c.Arrangement == ChargeArrangement.Unknown ? null : c.Arrangement;
        ev.SecurityClassificationConfidence = c.OverallConfidence;
        ev.SecurityMatchedRulesJson = c.MatchedRulesJson;

        foreach (var d in c.SecurityComponents)
            ev.SecurityComponents.Add(new ChargeSecurityComponent
            {
                RequestId = requestId,
                IngestionRunId = ingestionRunId,
                SecurityType = d.SecurityType,
                Ranking = d.Ranking,
                AssetDescriptionRaw = d.AssetPhrase,
                IsPrimarySecurity = d.IsPrimary,
                Confidence = d.Confidence,
                MatchedRule = d.MatchedRule
            });
    }

    private static RocCharge BuildChargeHeader(
        string chargeId, List<RocChargeEvent> chargeEvents, long requestId, long ingestionRunId, long? sourceDocumentId)
    {
        var latest = chargeEvents.OrderByDescending(e => e.EventDate).First();
        var creation = chargeEvents.FirstOrDefault(e => e.EventType == ChargeEventType.Creation);
        var satisfaction = chargeEvents.FirstOrDefault(e => e.EventType == ChargeEventType.Satisfaction);
        var lastModification = chargeEvents.Where(e => e.EventType == ChargeEventType.Modification)
            .OrderByDescending(e => e.EventDate).FirstOrDefault();

        // Rollup from the latest *classification-bearing* event — a Satisfaction event with no security
        // narrative must not null out a prior Modification's classification.
        var rollupEvent = chargeEvents
            .Where(e => e.SecurityClassificationConfidence is not null and not ChargeClassificationConfidence.None)
            .OrderByDescending(e => e.EventDate)
            .FirstOrDefault();

        return new RocCharge
        {
            RequestId = requestId,
            IngestionRunId = ingestionRunId,
            SourceDocumentId = sourceDocumentId,
            RocChargeNumber = chargeId,
            LatestChargeHolderRaw = latest.HolderNameRaw,
            LatestChargeHolderNormalized = latest.HolderNameNormalized,
            CreationDate = creation?.EventDate,
            CurrentAmount = latest.ChargeAmount,
            CurrentAmountRaw = latest.ChargeAmountRaw,
            ChargeStatus = satisfaction is not null ? "Satisfied" : "Open",
            LatestModificationDate = lastModification?.EventDate,
            SatisfactionDate = satisfaction?.EventDate,
            LatestPrimaryFacilityType = rollupEvent?.PrimaryFacilityType,
            LatestArrangement = rollupEvent?.Arrangement,
            LatestSecurityTypesJson = rollupEvent is null || rollupEvent.SecurityComponents.Count == 0
                ? null
                : System.Text.Json.JsonSerializer.Serialize(rollupEvent.SecurityComponents.Select(c => c.SecurityType.ToString()).Distinct()),
            LatestSecurityConfidence = rollupEvent?.SecurityClassificationConfidence,
            Events = chargeEvents
        };
    }

    private static object? Cell(IReadOnlyList<object?> row, int index) => index < row.Count ? row[index] : null;

    private static string? NullIfDash(object? value)
    {
        var text = value?.ToString()?.Trim();
        return string.IsNullOrEmpty(text) || text == "-" ? null : text;
    }

    private static bool? ParseYesNo(object? value) => value?.ToString()?.Trim().ToUpperInvariant() switch
    {
        "YES" => true,
        "NO" => false,
        _ => null
    };

    private static string? ParseChargeId(object? value)
    {
        return value switch
        {
            null => null,
            double d => ((long)d).ToString(),
            decimal dec => ((long)dec).ToString(),
            int i => i.ToString(),
            long l => l.ToString(),
            string s when s.Trim().Length > 0 && s.Trim() != "-" => s.Trim(),
            _ => null
        };
    }
}
