using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using MCAROC_Analysis.Models.Dossier;
using MCAROC_Analysis.Models.Registry;
using MCAROC_Analysis.Models.Viz;

namespace MCAROC_Analysis.Services.Registry;

public sealed class CategoryPointDto
{
    public string Category { get; set; } = string.Empty;
    public decimal Value { get; set; }
}

public sealed class YearPointDto
{
    public int Year { get; set; }
    public decimal Count { get; set; }
}

public sealed class RankedPercentDto
{
    public string Label { get; set; } = string.Empty;
    public int Count { get; set; }
    public double Percent { get; set; }
}

public sealed class RegistryAggregateSnapshotDto
{
    public RegistrySnapshotMetadata Metadata { get; set; } = new();
    public RegistryStatusMetrics OverallStatus { get; set; } = new();
    public RegistryStatusMetrics CompanyStatus { get; set; } = new();
    public RegistryStatusMetrics LlpStatus { get; set; } = new();
    public RegistryStatusMetrics ForeignStatus { get; set; } = new();

    public List<CategoryPointDto> TopStates { get; set; } = [];
    public List<CategoryPointDto> TopIndustries { get; set; } = [];
    public List<CategoryPointDto> ForeignCountries { get; set; } = [];
    public List<YearPointDto> CompanyRegistrationYears { get; set; } = [];
    public List<YearPointDto> LlpRegistrationYears { get; set; } = [];

    public List<RankedPercentDto> CompanyClasses { get; set; } = [];
    public List<RankedPercentDto> ListingStatusSplit { get; set; } = [];
    public List<RankedPercentDto> CapitalDistribution { get; set; } = [];

    public static RegistryAggregateSnapshotDto FromModel(RegistryAggregateData model)
    {
        var dto = new RegistryAggregateSnapshotDto
        {
            Metadata = model.Metadata,
            OverallStatus = model.OverallStatus,
            CompanyStatus = model.CompanyStatus,
            LlpStatus = model.LlpStatus,
            ForeignStatus = model.ForeignStatus,
            CompanyClasses = model.CompanyClasses.Select(c => new RankedPercentDto { Label = c.Label, Count = c.Count, Percent = c.Percent }).ToList(),
            ListingStatusSplit = model.ListingStatusSplit.Select(l => new RankedPercentDto { Label = l.Label, Count = l.Count, Percent = l.Percent }).ToList(),
            CapitalDistribution = model.CapitalDistribution.Select(k => new RankedPercentDto { Label = k.Range, Count = k.Count, Percent = k.Percent }).ToList()
        };

        if (model.TopStatesChart?.Points != null)
        {
            dto.TopStates = model.TopStatesChart.Points
                .Select(p => new CategoryPointDto { Category = p.Category, Value = p.Value })
                .ToList();
        }

        if (model.TopIndustriesChart?.Points != null)
        {
            dto.TopIndustries = model.TopIndustriesChart.Points
                .Select(p => new CategoryPointDto { Category = p.Category, Value = p.Value })
                .ToList();
        }

        if (model.ForeignCountriesChart?.Points != null)
        {
            dto.ForeignCountries = model.ForeignCountriesChart.Points
                .Select(p => new CategoryPointDto { Category = p.Category, Value = p.Value })
                .ToList();
        }

        if (model.CompanyRegistrationYearSeries?.Points != null)
        {
            dto.CompanyRegistrationYears = model.CompanyRegistrationYearSeries.Points
                .Select(p => new YearPointDto
                {
                    Year = p.Period.ActualDate?.Year ?? int.Parse(p.Period.Label, CultureInfo.InvariantCulture),
                    Count = p.Value ?? 0
                })
                .ToList();
        }

        if (model.LlpRegistrationYearSeries?.Points != null)
        {
            dto.LlpRegistrationYears = model.LlpRegistrationYearSeries.Points
                .Select(p => new YearPointDto
                {
                    Year = p.Period.ActualDate?.Year ?? int.Parse(p.Period.Label, CultureInfo.InvariantCulture),
                    Count = p.Value ?? 0
                })
                .ToList();
        }

        return dto;
    }

    public RegistryAggregateData ToModel()
    {
        var model = new RegistryAggregateData
        {
            Metadata = Metadata,
            OverallStatus = OverallStatus,
            CompanyStatus = CompanyStatus,
            LlpStatus = LlpStatus,
            ForeignStatus = ForeignStatus,
            CompanyClasses = CompanyClasses.Select(c => (c.Label, c.Count, c.Percent)).ToList(),
            ListingStatusSplit = ListingStatusSplit.Select(l => (l.Label, l.Count, l.Percent)).ToList(),
            CapitalDistribution = CapitalDistribution.Select(k => (k.Label, k.Count, k.Percent)).ToList()
        };

        if (TopStates.Count > 0)
        {
            var points = TopStates
                .Select(p => new ChartCategoryPoint(p.Category, p.Value, null, null, ChartAccent.Brand))
                .ToList();
            model.TopStatesChart = ChartCategorySeries.Create("Top States by Companies", MetricUnit.Count, ["CompanyMasterRecord.State"], points);
        }

        if (TopIndustries.Count > 0)
        {
            var points = TopIndustries
                .Select(p => new ChartCategoryPoint(p.Category, p.Value, null, null, ChartAccent.Brand))
                .ToList();
            model.TopIndustriesChart = ChartCategorySeries.Create("Top Industries", MetricUnit.Count, ["CompanyMasterRecord.IndustrialClassification"], points);
        }

        if (ForeignCountries.Count > 0)
        {
            var points = ForeignCountries
                .Select(p => new ChartCategoryPoint(p.Category, p.Value, null, null, ChartAccent.Warning))
                .ToList();
            model.ForeignCountriesChart = ChartCategorySeries.Create("Foreign Origin Countries", MetricUnit.Count, ["CompanyMasterRecord.Country"], points);
        }

        if (CompanyRegistrationYears.Count > 0)
        {
            var points = CompanyRegistrationYears
                .OrderBy(y => y.Year)
                .Select(y => new ChartTimePoint(ChartPeriod.ForDate(new DateOnly(y.Year, 1, 1), y.Year.ToString(CultureInfo.InvariantCulture)), y.Count))
                .ToList();
            model.CompanyRegistrationYearSeries = ChartSeries.Create("Company Incorporations by Year", MetricUnit.Count, ["CompanyMasterRecord.RegistrationDate"], points);
        }

        if (LlpRegistrationYears.Count > 0)
        {
            var points = LlpRegistrationYears
                .OrderBy(y => y.Year)
                .Select(y => new ChartTimePoint(ChartPeriod.ForDate(new DateOnly(y.Year, 1, 1), y.Year.ToString(CultureInfo.InvariantCulture)), y.Count))
                .ToList();
            model.LlpRegistrationYearSeries = ChartSeries.Create("LLP Incorporations by Year", MetricUnit.Count, ["CompanyMasterRecord.RegistrationDate"], points);
        }

        return model;
    }
}
