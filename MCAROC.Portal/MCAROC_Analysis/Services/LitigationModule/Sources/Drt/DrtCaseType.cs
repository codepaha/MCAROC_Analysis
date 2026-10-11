namespace MCAROC_Analysis.Services.LitigationModule.Sources.Drt;

/// <summary>DRT / DRAT case type definition.</summary>
/// <param name="Id">Numeric case type ID returned by <c>getDrtDratCaseTyepName</c>.</param>
/// <param name="Name">Case type name string (e.g. <c>Original Application</c>, <c>Misc Application</c>).</param>
/// <param name="Code">Canonical short code resolved via <see cref="MCAROC_Analysis.Services.LitigationModule.Normalisation.CaseTypeCodes"/> (e.g. <c>OA</c>, <c>MA</c>).</param>
public sealed record DrtCaseType(int Id, string Name, string? Code);

