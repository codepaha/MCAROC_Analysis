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

    // ── Valid JSON, wrong shape — TryGetProperty throws InvalidOperationException on a non-object
    // element, so these must be guarded explicitly, not just caught as a JsonException. ──

    [Fact]
    public void Json_array_root_yields_no_reference_not_a_crash()
    {
        Assert.Null(FindingSourceReference.TryParse("[]"));
    }

    [Fact]
    public void Json_null_literal_yields_no_reference_not_a_crash()
    {
        Assert.Null(FindingSourceReference.TryParse("null"));
    }

    [Fact]
    public void Json_scalar_number_yields_no_reference_not_a_crash()
    {
        Assert.Null(FindingSourceReference.TryParse("5"));
    }

    [Fact]
    public void Json_scalar_string_yields_no_reference_not_a_crash()
    {
        Assert.Null(FindingSourceReference.TryParse("\"hello\""));
    }

    [Fact]
    public void EntityIds_as_a_non_array_value_is_ignored_not_a_crash()
    {
        var reference = FindingSourceReference.TryParse("""{"entityType":"Litigation","entityIds":"not-an-array"}""");

        Assert.NotNull(reference);
        Assert.Empty(reference!.EntityIds);
        Assert.Equal("Computed value", reference.DisplayText());
    }

    // ── Numeric overflow inside entityIds/rows — GetInt64/GetInt32 throw FormatException on a value
    // that doesn't fit; TryGetInt64/TryGetInt32 must be used so an oversized id is dropped, not fatal. ──

    [Fact]
    public void Entity_id_too_large_for_int64_is_dropped_not_a_crash()
    {
        var reference = FindingSourceReference.TryParse(
            """{"entityType":"Litigation","entityIds":[10,99999999999999999999999999999,11]}""");

        Assert.NotNull(reference);
        Assert.Equal([10L, 11L], reference!.EntityIds);
        Assert.Equal("Computed from: 2 Litigation records", reference.DisplayText());
    }

    [Fact]
    public void Row_number_too_large_for_int32_is_dropped_not_a_crash()
    {
        var reference = FindingSourceReference.TryParse(
            """{"sheet":"Standalone Financial Data","rows":[14,99999999999,15]}""");

        Assert.NotNull(reference);
        Assert.Equal([14, 15], reference!.Rows);
        Assert.Equal("Computed from: Standalone Financial Data, rows 14, 15", reference.DisplayText());
    }

    [Fact]
    public void Non_numeric_entries_in_entityIds_are_skipped_not_a_crash()
    {
        var reference = FindingSourceReference.TryParse(
            """{"entityType":"Litigation","entityIds":[10,"oops",null,11]}""");

        Assert.NotNull(reference);
        Assert.Equal([10L, 11L], reference!.EntityIds);
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
