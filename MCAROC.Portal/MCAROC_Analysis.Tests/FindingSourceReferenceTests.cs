using MCAROC_Analysis.Models;

namespace MCAROC_Analysis.Tests;

/// <summary>Provenance for a computed/derived AnalysisFinding (#113, C3 branch b) — must never fabricate a
/// sheet/row citation when the real SourceReferenceJson (as ChargeRules/LitigationRules actually write it)
/// only carries entityType/entityIds, and must fall back to "Computed value" when there is nothing
/// identifiable at all.</summary>
public class FindingSourceReferenceTests
{
    [Fact]
    public void Null_json_yields_no_reference()
    {
        Assert.Null(FindingSourceReference.TryParse(null));
    }

    [Fact]
    public void Blank_json_yields_no_reference()
    {
        Assert.Null(FindingSourceReference.TryParse("   "));
    }

    [Fact]
    public void Malformed_json_yields_no_reference_not_a_crash()
    {
        Assert.Null(FindingSourceReference.TryParse("{not valid json"));
    }

    [Fact]
    public void Real_shape_entityType_and_entityIds_only_shows_record_count()
    {
        // The actual shape LitigationRules/ChargeRules write today — no sheet/rows.
        var reference = FindingSourceReference.TryParse("""{"entityType":"Litigation","entityIds":[10,11,12]}""");

        Assert.NotNull(reference);
        Assert.Equal("Computed from: 3 Litigation records", reference!.DisplayText());
    }

    [Fact]
    public void Single_entity_id_uses_singular_record()
    {
        var reference = FindingSourceReference.TryParse("""{"entityType":"RocCharge","entityIds":[7]}""");

        Assert.Equal("Computed from: 1 RocCharge record", reference!.DisplayText());
    }

    [Fact]
    public void Aspirational_sheet_and_rows_shape_is_used_when_present()
    {
        // The doc-comment's full shape — no rule populates this today, but if one ever does, prefer it
        // over the coarser entityType/entityIds count.
        var reference = FindingSourceReference.TryParse(
            """{"entityType":"FinancialYearData","entityIds":[12,13],"sourceDocumentId":2,"sheet":"Standalone Financial Data","rows":[14,15]}""");

        Assert.Equal("Computed from: Standalone Financial Data, rows 14, 15", reference!.DisplayText());
    }

    [Fact]
    public void Sheet_without_rows_omits_the_row_clause()
    {
        var reference = FindingSourceReference.TryParse("""{"sheet":"About the Company"}""");

        Assert.Equal("Computed from: About the Company", reference!.DisplayText());
    }

    [Fact]
    public void Json_with_no_identifying_fields_yields_no_reference()
    {
        Assert.Null(FindingSourceReference.TryParse("""{"sourceDocumentId":2}"""));
    }

    [Fact]
    public void Missing_reference_falls_back_to_computed_value_label_at_the_call_site()
    {
        // Mirrors the exact fallback expression used in _AiAnalysisTab.cshtml.
        var displayed = FindingSourceReference.TryParse(null)?.DisplayText() ?? "Computed value";

        Assert.Equal("Computed value", displayed);
    }
}
