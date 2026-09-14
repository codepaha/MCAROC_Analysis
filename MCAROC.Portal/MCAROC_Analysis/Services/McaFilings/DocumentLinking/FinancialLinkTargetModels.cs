using MCAROC_Analysis.Data.Entities;

namespace MCAROC_Analysis.Services.McaFilings.DocumentLinking;

public enum FinancialTargetKind
{
    FinancialYearData,
    FinancialFact
}

/// <summary>
/// Exact 2D physical workbook coordinates for a cell in the financial data sheet.
/// </summary>
public record TargetSourceCoordinates(
    string SheetName,
    int SourceRowNumber,
    int SourceColumnNumber,
    string SourceColumnHeader,
    string? RowLabel,
    string? TargetField,
    int? FinancialYear
);

/// <summary>
/// Durable in-memory projection of an extractable financial target produced directly
/// by the authoritative parser, binding to the exact entity reference.
/// </summary>
public class FinancialLinkTarget
{
    public FinancialTargetKind TargetKind { get; init; }
    public object TargetEntity { get; init; } = default!;
    public int FinancialYear { get; init; }
    public FinancialBasis Basis { get; init; }
    public string TargetField { get; init; } = string.Empty;
    public decimal? NumericValue { get; init; }
    public string? RawValue { get; init; }
    public TargetSourceCoordinates Coordinates { get; init; } = default!;
    public bool YearInferred { get; init; }
}

/// <summary>
/// Fast in-memory catalog of all financial targets emitted by the workbook parser.
/// </summary>
public class FinancialLinkTargetCatalog
{
    private readonly List<FinancialLinkTarget> _targets = [];
    private readonly Dictionary<(int Year, FinancialBasis Basis), List<FinancialLinkTarget>> _byYearAndBasis = [];

    public IReadOnlyList<FinancialLinkTarget> AllTargets => _targets;

    public void Add(FinancialLinkTarget target)
    {
        _targets.Add(target);
        var key = (target.FinancialYear, target.Basis);
        if (!_byYearAndBasis.TryGetValue(key, out var list))
        {
            list = [];
            _byYearAndBasis[key] = list;
        }
        list.Add(target);
    }

    public void AddRange(IEnumerable<FinancialLinkTarget> targets)
    {
        foreach (var t in targets) Add(t);
    }

    public IReadOnlyList<FinancialLinkTarget> GetTargets(int financialYear, FinancialBasis basis)
    {
        if (_byYearAndBasis.TryGetValue((financialYear, basis), out var list))
            return list;
        return Array.Empty<FinancialLinkTarget>();
    }

    public FinancialLinkTarget? GetTarget(int financialYear, FinancialBasis basis, string field)
    {
        if (_byYearAndBasis.TryGetValue((financialYear, basis), out var list))
        {
            return list.FirstOrDefault(t => string.Equals(t.TargetField, field, StringComparison.OrdinalIgnoreCase));
        }
        return null;
    }

    /// <summary>
    /// Allocates synthetic IDs for unpersisted entities using non-mutating ReferenceEqualityComparer reference maps,
    /// starting strictly above any existing maximum non-zero ID in deterministic parser order.
    /// Preserves entity immutability and repeatability across runs.
    /// </summary>
    public static (Dictionary<FinancialYearData, long> FinMap, Dictionary<FinancialFact, long> FactMap) BuildIdMaps(
        IEnumerable<FinancialYearData> financialYears,
        IEnumerable<FinancialFact> financialFacts)
    {
        long maxExistingFinId = 0;
        foreach (var f in financialYears)
        {
            if (f.FinancialId > maxExistingFinId) maxExistingFinId = f.FinancialId;
        }

        long maxExistingFactId = 0;
        foreach (var fact in financialFacts)
        {
            if (fact.FinancialFactId > maxExistingFactId) maxExistingFactId = fact.FinancialFactId;
        }

        var finMap = new Dictionary<FinancialYearData, long>(ReferenceEqualityComparer.Instance);
        long nextFinId = maxExistingFinId;
        foreach (var f in financialYears)
        {
            if (!finMap.ContainsKey(f))
            {
                finMap[f] = f.FinancialId != 0 ? f.FinancialId : ++nextFinId;
            }
        }

        var factMap = new Dictionary<FinancialFact, long>(ReferenceEqualityComparer.Instance);
        long nextFactId = maxExistingFactId;
        foreach (var fact in financialFacts)
        {
            if (!factMap.ContainsKey(fact))
            {
                factMap[fact] = fact.FinancialFactId != 0 ? fact.FinancialFactId : ++nextFactId;
            }
        }

        return (finMap, factMap);
    }
}
