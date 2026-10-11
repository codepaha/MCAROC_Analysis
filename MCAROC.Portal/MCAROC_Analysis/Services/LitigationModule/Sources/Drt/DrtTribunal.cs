namespace MCAROC_Analysis.Services.LitigationModule.Sources.Drt;

/// <summary>Authoritative DRT / DRAT tribunal record (plan section 2.1 table).</summary>
/// <param name="Id">Numeric schema ID (<c>schemeNameDrtId</c>) used by the portal API (1..44 for DRT, 100..104 for DRAT).</param>
/// <param name="Name">Canonical tribunal name as reported by <c>getDrtDratScheamName</c>.</param>
/// <param name="IsDrat">True for appellate tribunals (<c>Id >= 100</c>).</param>
/// <param name="State">Authoritative state derived from the tribunal registry (portal does not return state).</param>
/// <param name="District">Authoritative district derived from the tribunal registry.</param>
/// <param name="Bench">Bench identifier (e.g. <c>DRT 1</c>, <c>DRT 2</c>, or city name).</param>
public sealed record DrtTribunal(int Id, string Name, bool IsDrat, string State, string District, string Bench);

