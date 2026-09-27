using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace MCAROC_Analysis.Models;

// The extraction contract is intentionally limited to borrower and requesting-branch data.
public sealed class BorrowerAssignmentDetails
{
    [JsonPropertyName("request_details")] public BorrowerRequestDetails RequestDetails { get; set; } = new();
    [JsonPropertyName("requesting_branch_details")] public BorrowerBranchDetails RequestingBranchDetails { get; set; } = new();
    [JsonPropertyName("company_details")] public BorrowerCompanyDetails CompanyDetails { get; set; } = new();
}

public sealed class BorrowerRequestDetails
{
    [JsonPropertyName("document_title"), StringLength(250)] public string? DocumentTitle { get; set; }
    [JsonPropertyName("date_of_request"), StringLength(30)] public string? DateOfRequest { get; set; }
}

public sealed class BorrowerBranchDetails
{
    [JsonPropertyName("branch_name"), StringLength(250)] public string? BranchName { get; set; }
    [JsonPropertyName("branch_code"), RegularExpression(@"\d{1,10}")] public string? BranchCode { get; set; }
    [JsonPropertyName("address"), StringLength(2000)] public string? Address { get; set; }
    [JsonPropertyName("contact_person")] public BorrowerContactPerson ContactPerson { get; set; } = new();
}

public sealed class BorrowerContactPerson
{
    [JsonPropertyName("name"), StringLength(250)] public string? Name { get; set; }
    [JsonPropertyName("designation"), StringLength(250)] public string? Designation { get; set; }
    [JsonPropertyName("phone_landline"), StringLength(50)] public string? PhoneLandline { get; set; }
    [JsonPropertyName("phone_mobile"), StringLength(50)] public string? PhoneMobile { get; set; }
    [JsonPropertyName("email"), EmailAddress, StringLength(250)] public string? Email { get; set; }
}

public sealed class BorrowerCompanyDetails
{
    [JsonPropertyName("company_name"), StringLength(250)] public string? CompanyName { get; set; }
    [JsonPropertyName("entity_type"), StringLength(100)] public string? EntityType { get; set; }
    [JsonPropertyName("cin"), RegularExpression(@"[UL][0-9]{5}[A-Z]{2}[0-9]{4}[A-Z]{3}[0-9]{6}")] public string? Cin { get; set; }
    [JsonPropertyName("pan"), RegularExpression(@"[A-Z]{5}[0-9]{4}[A-Z]")] public string? Pan { get; set; }
    [JsonPropertyName("gstin"), RegularExpression(@"[0-9]{2}[A-Z]{5}[0-9]{4}[A-Z][A-Z0-9]Z[A-Z0-9]")] public string? Gstin { get; set; }
    [JsonPropertyName("incorporation_date"), StringLength(30)] public string? IncorporationDate { get; set; }
    [JsonPropertyName("registered_office_address"), StringLength(2000)] public string? RegisteredOfficeAddress { get; set; }
    [JsonPropertyName("plant_address"), StringLength(2000)] public string? PlantAddress { get; set; }
    [JsonPropertyName("directors_or_partners"), MaxLength(30)] public List<BorrowerPersonDetails> DirectorsOrPartners { get; set; } = [];
}

public sealed class BorrowerPersonDetails
{
    [JsonPropertyName("name"), StringLength(250)] public string? Name { get; set; }
    [JsonPropertyName("pan"), RegularExpression(@"[A-Z]{5}[0-9]{4}[A-Z]")] public string? Pan { get; set; }
    [JsonPropertyName("din"), RegularExpression(@"[0-9]{8}")] public string? Din { get; set; }
}
