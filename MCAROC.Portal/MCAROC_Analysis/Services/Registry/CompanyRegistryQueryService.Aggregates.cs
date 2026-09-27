using System.Data;
using System.Globalization;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Models.Dossier;
using MCAROC_Analysis.Models.Registry;
using MCAROC_Analysis.Models.Viz;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace MCAROC_Analysis.Services.Registry;

public sealed partial class CompanyRegistryQueryService
{
    // GROUPING SETS shares the master-table input across dimensions. Only grouped counts leave SQL.
    private const string AggregateSql = """
        WITH master AS (
            SELECT RecordType, Status, State, IndustrialClassification AS Industry, Roc, District,
                DATEPART(year, RegistrationDate) AS RegistrationYear,
                DATEPART(month, RegistrationDate) AS RegistrationMonth, Class, ListingStatus,
                CASE WHEN AuthorizedCapital IS NULL THEN NULL WHEN AuthorizedCapital < 100000 THEN 0
                    WHEN AuthorizedCapital < 1000000 THEN 1 WHEN AuthorizedCapital < 10000000 THEN 2
                    WHEN AuthorizedCapital < 100000000 THEN 3 WHEN AuthorizedCapital < 1000000000 THEN 4 ELSE 5 END AS CapitalBand,
                Country
            FROM dbo.CompanyMasterRecords
        )
        SELECT RecordType,
            CASE WHEN GROUPING(Status)=0 THEN 'StateStatus' WHEN GROUPING(Industry)=0 THEN 'Industry'
                WHEN GROUPING(Roc)=0 THEN 'Roc' WHEN GROUPING(District)=0 THEN 'District'
                WHEN GROUPING(RegistrationYear)=0 THEN 'Registration' WHEN GROUPING(Class)=0 THEN 'Class'
                WHEN GROUPING(ListingStatus)=0 THEN 'Listing' WHEN GROUPING(CapitalBand)=0 THEN 'Capital'
                ELSE 'Country' END AS Dimension,
            Status, State, Industry, Roc, District, RegistrationYear, RegistrationMonth,
            Class, ListingStatus, CapitalBand, Country, COUNT_BIG(*) AS EntityCount
        FROM master
        GROUP BY GROUPING SETS (
            (RecordType, Status, State), (RecordType, Industry), (RecordType, Roc), (RecordType, District),
            (RecordType, RegistrationYear, RegistrationMonth), (RecordType, Class),
            (RecordType, ListingStatus), (RecordType, CapitalBand), (RecordType, Country))
        """;

    private sealed record AggregateRow(CompanyMasterRecordType Type, string Dimension, string? Label,
        string? State, int? Year, int? Month, int Count);

    private async Task<RegistryAggregateData> BuildAggregateCoreAsync(CompanyMasterSyncJob? job, CancellationToken ct)
    {
        var rows = new List<AggregateRow>();
        var connection = _db.Database.GetDbConnection();
        var openedHere = connection.State != ConnectionState.Open;
        if (openedHere) await _db.Database.OpenConnectionAsync(ct);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = AggregateSql;
            command.Transaction = _db.Database.CurrentTransaction?.GetDbTransaction();
            command.CommandTimeout = _db.Database.GetCommandTimeout() ?? 300;
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var type = Enum.Parse<CompanyMasterRecordType>(reader.GetString(0), ignoreCase: true);
                var dimension = reader.GetString(1);
                string? Text(int index) => reader.IsDBNull(index) ? null : reader.GetString(index).Trim();
                int? Number(int index) => reader.IsDBNull(index) ? null : reader.GetInt32(index);
                var label = dimension switch {
                    "StateStatus" => Text(2), "Industry" => Text(4), "Roc" => Text(5), "District" => Text(6),
                    "Class" => Text(9), "Listing" => Text(10), "Capital" => Number(11)?.ToString(),
                    "Country" => Text(12), _ => null };
                rows.Add(new(type, dimension, label, Text(3), Number(7), Number(8), checked((int)reader.GetInt64(13))));
            }
        }
        finally { if (openedHere) await _db.Database.CloseConnectionAsync(); }

        List<RegistryMetricBucket> Buckets(IEnumerable<AggregateRow> source, Func<AggregateRow, string?> label) => source
            .Where(r => !string.IsNullOrWhiteSpace(label(r))).GroupBy(r => label(r)!, StringComparer.OrdinalIgnoreCase)
            .Select(g => new RegistryMetricBucket(g.Key, g.Sum(r => r.Count)))
            .OrderByDescending(r => r.Count).ThenBy(r => r.Label, StringComparer.OrdinalIgnoreCase).ToList();
        var entities = Enum.GetValues<CompanyMasterRecordType>().Select(type =>
        {
            var records = rows.Where(r => r.Type == type).ToList();
            var statuses = records.Where(r => r.Dimension == "StateStatus").ToList();
            var states = Buckets(statuses, r => r.State).Take(10).ToList();
            List<RegistryMetricBucket> Dimension(string dimension) => Buckets(records.Where(r => r.Dimension == dimension), r => r.Label).Take(10).ToList();
            return new RegistryEntityAnalytics {
                RecordType = type,
                Statuses = Buckets(statuses, r => string.IsNullOrWhiteSpace(r.Label) ? "Unknown" : r.Label),
                States = states, Industries = Dimension("Industry"), Rocs = Dimension("Roc"), Districts = Dimension("District"),
                DistressedStates = Buckets(statuses.Where(r => RegistryEntityAnalytics.IsDistressed(r.Label)), r => r.State).Take(10).ToList(),
                StateStatuses = statuses.Where(r => states.Any(s => string.Equals(s.Label, r.State, StringComparison.OrdinalIgnoreCase)))
                    .Select(r => new RegistryStateStatus(states.First(s => string.Equals(s.Label, r.State, StringComparison.OrdinalIgnoreCase)).Label,
                        r.Label ?? "Unknown", r.Count)).ToList(),
                Registrations = records.Where(r => r.Dimension == "Registration" && r.Year.HasValue && r.Month.HasValue)
                    .OrderBy(r => r.Year).ThenBy(r => r.Month).Select(r => new RegistryRegistrationPeriod(r.Year!.Value, r.Month!.Value, r.Count)).ToList()
            };
        }).ToList();
        RegistryStatusMetrics Status(CompanyMasterRecordType type) {
            var buckets = entities.Single(a => a.RecordType == type).Statuses;
            return MapStatusMetrics(buckets.Select(b => ((string?)b.Label, b.Count)), new(buckets.Select(b => b.Label), StringComparer.OrdinalIgnoreCase), type == CompanyMasterRecordType.Foreign);
        }
        var data = new RegistryAggregateData {
            EntityAnalytics = entities,
            Metadata = new() { PublishedDate = job?.PublishedDate, CompletedUtc = job?.CompletedUtc,
                CalculatedUtc = DateTime.UtcNow, IsImportedBaseline = job == null },
            CompanyStatus = Status(CompanyMasterRecordType.Company), LlpStatus = Status(CompanyMasterRecordType.Llp), ForeignStatus = Status(CompanyMasterRecordType.Foreign)
        };
        data.OverallStatus = new RegistryStatusMetrics {
            Active = data.CompanyStatus.Active + data.LlpStatus.Active + data.ForeignStatus.Active,
            StrikeOff = data.CompanyStatus.StrikeOff + data.LlpStatus.StrikeOff + data.ForeignStatus.StrikeOff,
            UnderCirp = data.CompanyStatus.UnderCirp + data.LlpStatus.UnderCirp,
            UnderLiquidation = data.CompanyStatus.UnderLiquidation + data.LlpStatus.UnderLiquidation,
            OtherUnclassified = data.CompanyStatus.OtherUnclassified + data.LlpStatus.OtherUnclassified + data.ForeignStatus.OtherUnclassified,
            HasObservedCirp = data.CompanyStatus.HasObservedCirp || data.LlpStatus.HasObservedCirp,
            HasObservedLiquidation = data.CompanyStatus.HasObservedLiquidation || data.LlpStatus.HasObservedLiquidation
        };
        data.Metadata.CompanyCount = data.CompanyStatus.Total;
        data.Metadata.LlpCount = data.LlpStatus.Total;
        data.Metadata.ForeignCount = data.ForeignStatus.Total;
        data.Metadata.TotalRecords = data.OverallStatus.Total;
        ChartCategorySeries? Chart(string title, IEnumerable<RegistryMetricBucket> buckets) {
            var points = buckets.Select(b => new ChartCategoryPoint(b.Label, b.Count, null, null)).ToList();
            return points.Count == 0 ? null : ChartCategorySeries.Create(title, MetricUnit.Count, ["CompanyMasterRecords"], points);
        }
        var company = entities.Single(a => a.RecordType == CompanyMasterRecordType.Company);
        data.TopStatesChart = Chart("Top states · Companies", company.States);
        data.TopIndustriesChart = Chart("Top industries · Companies", company.Industries);
        data.ForeignCountriesChart = Chart("Foreign origin countries", Buckets(rows.Where(r => r.Type == CompanyMasterRecordType.Foreign && r.Dimension == "Country"), r => r.Label).Take(10));
        ChartSeries? Years(CompanyMasterRecordType type) {
            var points = entities.Single(a => a.RecordType == type).Registrations.Where(r => r.Year >= 2000).GroupBy(r => r.Year).OrderBy(g => g.Key)
                .Select(g => new ChartTimePoint(ChartPeriod.ForDate(new DateOnly(g.Key, 1, 1), g.Key.ToString(CultureInfo.InvariantCulture)), g.Sum(r => r.Count))).ToList();
            return points.Count == 0 ? null : ChartSeries.Create("Registrations by year", MetricUnit.Count, ["CompanyMasterRecord.RegistrationDate"], points);
        }
        data.CompanyRegistrationYearSeries = Years(CompanyMasterRecordType.Company);
        data.LlpRegistrationYearSeries = Years(CompanyMasterRecordType.Llp);
        var total = Math.Max(1, data.CompanyStatus.Total);
        List<(string Label, int Count, double Percent)> Percent(string dimension) => Buckets(rows.Where(r => r.Type == CompanyMasterRecordType.Company && r.Dimension == dimension), r => r.Label)
            .Select(b => (b.Label, b.Count, b.Count * 100d / total)).ToList();
        data.CompanyClasses = Percent("Class"); data.ListingStatusSplit = Percent("Listing");
        var bands = new[] { "< ₹1 Lakh", "₹1L – ₹10L", "₹10L – ₹1 Crore", "₹1Cr – ₹10 Crore", "₹10Cr – ₹100 Crore", "≥ ₹100 Crore" };
        data.CapitalDistribution = Enumerable.Range(0, bands.Length).Select(i => {
            var count = rows.Where(r => r.Type == CompanyMasterRecordType.Company && r.Dimension == "Capital" && r.Label == i.ToString()).Sum(r => r.Count);
            return (bands[i], count, count * 100d / total);
        }).ToList();
        return data;
    }
}
