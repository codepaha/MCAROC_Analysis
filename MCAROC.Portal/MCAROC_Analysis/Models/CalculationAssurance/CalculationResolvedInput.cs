namespace MCAROC_Analysis.Models.CalculationAssurance;

/// <summary>One resolved instance of a metric input — the specific source row and its actual value.
/// CalculationSourceRowRefResolver derives citation refs from these; CalculationInputCanonicalizer
/// derives the InputHash payload from the exact same resolution pass (CalculationInputResolver), so the
/// two can never silently drift apart on which entity types or rows they resolve — the bug that let a
/// metric mixing a resolved and an unresolved input hash and cite as if fully traceable.</summary>
public sealed record CalculationResolvedInput(
    string EntityType,
    long EntityId,
    string FieldOrLabel,
    string? ValueText,
    long? SourceDocumentId,
    string? SourceSheetName,
    int? SourceRowNumber);
