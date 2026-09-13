namespace MCAROC_Analysis.Models;

/// <summary>Feeds <c>Details/_Amount.cshtml</c> — a crore-denominated amount that the client-side
/// Crore/Lakh/₹ unit toggle (#123) can rewrite in place. The canonical value is always the crore decimal
/// already used everywhere else in this codebase (matching <see cref="DetailsFormat.Money"/>'s own
/// unit).</summary>
public sealed record AmountViewModel(decimal? ValueCrore);
