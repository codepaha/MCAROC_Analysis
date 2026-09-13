namespace MCAROC_Analysis.Models.CalculationAssurance;

/// <summary>One traceable source row backing a CalculationLedgerEntry's value. Deliberately not a reuse
/// of FindingSourceReference — that type's sheet/row shape is documented as aspirational and never
/// populated by any rule today; this type is populated for real, from the same ExtractedEntityBase
/// provenance fields (SourceDocumentId/SourceSheetName/SourceRowNumber) every ingested entity already
/// carries.</summary>
public sealed record CalculationSourceRowRef(
    string EntityType,
    long EntityId,
    long? SourceDocumentId,
    string? SourceSheetName,
    int? SourceRowNumber);
