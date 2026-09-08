using System.Text.Json.Serialization;

namespace MCAROC_Analysis.Services.McaFilings;

/// <summary>Wraps an extracted value with the page(s) it came from, per the review's traceability
/// requirement — Gemini is instructed to populate both, or leave Value null with empty Pages when a fact
/// isn't stated (never inferred).</summary>
public record EvidenceField<T>(
    [property: JsonPropertyName("value")] T? Value,
    [property: JsonPropertyName("pages")] List<int>? Pages);

/// <summary>The small schema family (per review point 8): a compromise between one generic prompt for
/// everything and 15 independent per-form-type prompts. Selected by the filing's dominant Category/FormType.</summary>
public static class FilingSchemaNames
{
    public const string Charge = "CHARGE_GENERIC";
    public const string CorporateResolutionFiling = "COMPLIANCE_RESOLUTION";
    public const string Auditor = "COMPLIANCE_AUDITOR";
    public const string RegisteredOffice = "COMPLIANCE_REGISTERED_OFFICE";
    public const string CapitalAllotment = "COMPLIANCE_CAPITAL_ALLOTMENT";
    public const string Constitutional = "CONSTITUTIONAL_GENERIC";
    public const string SchemaVersion = "1.0";
}

public class ChargeExtractionResult
{
    public EvidenceField<string>? ChargeIdOrSrn { get; set; }
    /// <summary>"Creation" / "Modification" / "Satisfaction".</summary>
    public EvidenceField<string>? EventType { get; set; }
    /// <summary>ISO 8601 date string, e.g. "2026-03-10".</summary>
    public EvidenceField<string>? EventDate { get; set; }
    public EvidenceField<string>? HolderName { get; set; }
    public EvidenceField<decimal>? AmountCrore { get; set; }
    public EvidenceField<string>? InterestRate { get; set; }
    public EvidenceField<string>? PropertyDescription { get; set; }
    public EvidenceField<bool>? IsSatisfactionLetter { get; set; }
    public EvidenceField<string>? SatisfactionDate { get; set; }

    public const string JsonShape = """
        {
          "chargeIdOrSrn": {"value": string|null, "pages": [int]},
          "eventType": {"value": "Creation"|"Modification"|"Satisfaction"|null, "pages": [int]},
          "eventDate": {"value": "YYYY-MM-DD"|null, "pages": [int]},
          "holderName": {"value": string|null, "pages": [int]},
          "amountCrore": {"value": number|null, "pages": [int]},
          "interestRate": {"value": string|null, "pages": [int]},
          "propertyDescription": {"value": string|null, "pages": [int]},
          "isSatisfactionLetter": {"value": boolean|null, "pages": [int]},
          "satisfactionDate": {"value": "YYYY-MM-DD"|null, "pages": [int]}
        }
        """;
}

public class CorporateResolutionExtractionResult
{
    public EvidenceField<string>? FormType { get; set; }
    public EvidenceField<string>? ResolutionType { get; set; }
    public EvidenceField<string>? FilingDate { get; set; }
    public EvidenceField<string>? Srn { get; set; }
    public EvidenceField<List<string>>? KeyParties { get; set; }

    public const string JsonShape = """
        {
          "formType": {"value": string|null, "pages": [int]},
          "resolutionType": {"value": string|null, "pages": [int]},
          "filingDate": {"value": "YYYY-MM-DD"|null, "pages": [int]},
          "srn": {"value": string|null, "pages": [int]},
          "keyParties": {"value": [string]|null, "pages": [int]}
        }
        """;
}

public class AuditorExtractionResult
{
    public EvidenceField<string>? AuditorName { get; set; }
    /// <summary>"Appointment" / "Resignation" / "Reappointment".</summary>
    public EvidenceField<string>? AppointmentType { get; set; }
    public EvidenceField<string>? EffectiveDate { get; set; }
    public EvidenceField<string>? Srn { get; set; }

    public const string JsonShape = """
        {
          "auditorName": {"value": string|null, "pages": [int]},
          "appointmentType": {"value": "Appointment"|"Resignation"|"Reappointment"|null, "pages": [int]},
          "effectiveDate": {"value": "YYYY-MM-DD"|null, "pages": [int]},
          "srn": {"value": string|null, "pages": [int]}
        }
        """;
}

public class RegisteredOfficeExtractionResult
{
    public EvidenceField<string>? ChangeType { get; set; }
    public EvidenceField<string>? EffectiveDate { get; set; }
    public EvidenceField<string>? OrderOrAddressDetails { get; set; }

    public const string JsonShape = """
        {
          "changeType": {"value": string|null, "pages": [int]},
          "effectiveDate": {"value": "YYYY-MM-DD"|null, "pages": [int]},
          "orderOrAddressDetails": {"value": string|null, "pages": [int]}
        }
        """;
}

public class CapitalAllotmentExtractionResult
{
    public EvidenceField<string>? AllotmentDate { get; set; }
    public EvidenceField<string>? SecuritiesType { get; set; }
    public EvidenceField<decimal>? AmountCrore { get; set; }
    public EvidenceField<int>? AllotteeCount { get; set; }

    public const string JsonShape = """
        {
          "allotmentDate": {"value": "YYYY-MM-DD"|null, "pages": [int]},
          "securitiesType": {"value": string|null, "pages": [int]},
          "amountCrore": {"value": number|null, "pages": [int]},
          "allotteeCount": {"value": int|null, "pages": [int]}
        }
        """;
}

/// <summary>Deliberately narrow — company name, state, main objects, capital clause, amendment dates only.
/// NOT a full free-text legal summary of the MoA/AoA (per review point 27: keeps token usage and
/// hallucination risk down).</summary>
public class ConstitutionalExtractionResult
{
    public EvidenceField<string>? DocumentType { get; set; }
    public EvidenceField<string>? CompanyName { get; set; }
    public EvidenceField<string>? RegisteredState { get; set; }
    public EvidenceField<string>? MainObjects { get; set; }
    public EvidenceField<string>? CapitalClause { get; set; }
    public EvidenceField<string>? AmendmentDate { get; set; }

    public const string JsonShape = """
        {
          "documentType": {"value": "MoA"|"AoA"|"Certificate of Incorporation"|null, "pages": [int]},
          "companyName": {"value": string|null, "pages": [int]},
          "registeredState": {"value": string|null, "pages": [int]},
          "mainObjects": {"value": string|null, "pages": [int]},
          "capitalClause": {"value": string|null, "pages": [int]},
          "amendmentDate": {"value": "YYYY-MM-DD"|null, "pages": [int]}
        }
        """;
}
