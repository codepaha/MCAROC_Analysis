using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Models.CalculationAssurance;
using MCAROC_Analysis.Models.Dossier;

namespace MCAROC_Analysis.Services.CalculationAssurance;

/// <summary>Resolves a MetricResult's inputs to real source rows for citation, via
/// CalculationInputResolver — the same resolution pass CalculationInputCanonicalizer hashes from, so the
/// two can never disagree on which inputs are traceable. See CalculationInputResolver's own doc comment
/// for exactly which entity types/input shapes resolve.</summary>
public static class CalculationSourceRowRefResolver
{
    public static IReadOnlyList<CalculationSourceRowRef> Resolve(
        IReadOnlyList<string> inputs, DossierModel model, CompanyProfile? companyProfile, IngestionRun? ingestionRun = null) =>
        inputs
            .SelectMany(input => CalculationInputResolver.ResolveOne(input, model, companyProfile, ingestionRun))
            .Select(r => new CalculationSourceRowRef(r.EntityType, r.EntityId, r.SourceDocumentId, r.SourceSheetName, r.SourceRowNumber))
            .DistinctBy(r => (r.EntityType, r.EntityId))
            .ToList();
}
