using System;
using System.Collections.Generic;
using MCAROC_Analysis.Data.Entities;
using MCAROC_Analysis.Models.Viz;

namespace MCAROC_Analysis.Models.Registry;

public enum RegistrySnapshotState
{
    /// <summary>Verified snapshot provenance established from a completed CompanyMasterSyncJob.</summary>
    VerifiedSnapshot,

    /// <summary>Active promotion in progress while local memory cache is cold; table scan refused.</summary>
    SyncColdUnavailable,

    /// <summary>Records exist in CompanyMasterRecords, but zero completed sync jobs exist; aggregates withheld.</summary>
    UnverifiedLegacyImport,

    /// <summary>Zero records and zero sync jobs.</summary>
    EmptyRegistry
}

public sealed class RegistrySnapshotMetadata
{
    public DateOnly? PublishedDate { get; set; }
    public DateTime? CompletedUtc { get; set; }
    public string Source { get; set; } = "MCA Corporate Data Management (mcacdm.nic.in)";
    public bool IsSyncInProgress { get; set; }
    public CompanyMasterSyncJobStatus? ActiveSyncStatus { get; set; }
    public long? ActiveJobId { get; set; }
    public long TotalRecords { get; set; }
    public long CompanyCount { get; set; }
    public long LlpCount { get; set; }
    public long ForeignCount { get; set; }
}

public sealed class RegistryStatusMetrics
{
    public int Active { get; set; }
    public int StrikeOff { get; set; }
    public int UnderCirp { get; set; }
    public int UnderLiquidation { get; set; }
    public int OtherUnclassified { get; set; }
    public int Total => Active + StrikeOff + UnderCirp + UnderLiquidation + OtherUnclassified;

    public bool HasObservedCirp { get; set; }
    public bool HasObservedLiquidation { get; set; }

    public double ActivePercent => Total > 0 ? (double)Active / Total * 100.0 : 0.0;
    public double StrikeOffPercent => Total > 0 ? (double)StrikeOff / Total * 100.0 : 0.0;
    public double CirpPercent => Total > 0 && HasObservedCirp ? (double)UnderCirp / Total * 100.0 : 0.0;
    public double LiquidationPercent => Total > 0 && HasObservedLiquidation ? (double)UnderLiquidation / Total * 100.0 : 0.0;
    public double OtherPercent => Total > 0 ? (double)OtherUnclassified / Total * 100.0 : 0.0;
}

public sealed class RegistryAggregateData
{
    public RegistrySnapshotMetadata Metadata { get; set; } = new();
    public RegistryStatusMetrics OverallStatus { get; set; } = new();
    public RegistryStatusMetrics CompanyStatus { get; set; } = new();
    public RegistryStatusMetrics LlpStatus { get; set; } = new();
    public RegistryStatusMetrics ForeignStatus { get; set; } = new();

    public ChartCategorySeries? TopStatesChart { get; set; }
    public ChartCategorySeries? TopIndustriesChart { get; set; }
    public ChartCategorySeries? ForeignCountriesChart { get; set; }
    public ChartSeries? CompanyRegistrationYearSeries { get; set; }
    public ChartSeries? LlpRegistrationYearSeries { get; set; }

    public IReadOnlyList<(string Label, int Count, double Percent)> CompanyClasses { get; set; } = [];
    public IReadOnlyList<(string Label, int Count, double Percent)> ListingStatusSplit { get; set; } = [];
    public IReadOnlyList<(string Range, int Count, double Percent)> CapitalDistribution { get; set; } = [];
}

public sealed class RegistryExplorerCriteria
{
    public string? Q { get; set; }
    public CompanyMasterRecordType? RecordType { get; set; }
    public string? CursorName { get; set; }
    public string? CursorIdentifier { get; set; }
    public int PageSize { get; set; } = 50;

    // Secondary filters unsupported in v1:
    public string? State { get; set; }
    public string? Status { get; set; }
    public string? Industry { get; set; }
    public string? Class { get; set; }
    public int? Year { get; set; }

    public bool HasSecondaryFilters =>
        !string.IsNullOrWhiteSpace(State) ||
        !string.IsNullOrWhiteSpace(Status) ||
        !string.IsNullOrWhiteSpace(Industry) ||
        !string.IsNullOrWhiteSpace(Class) ||
        Year.HasValue;
}

public sealed class RegistryRecordRow
{
    public string Identifier { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public CompanyMasterRecordType RecordType { get; set; }
    public string? Status { get; set; }
    public DateOnly? RegistrationDate { get; set; }
    public string? State { get; set; }
    public string? Roc { get; set; }
    public string? Class { get; set; }
    public string? Industry { get; set; }
    public string? Address { get; set; }
    public string? PinCode { get; set; }
    public string? ListingStatus { get; set; }
    public string? Country { get; set; }

    public bool IsEligibleForAutoFetch => RecordType != CompanyMasterRecordType.Foreign;
}

public sealed class RegistryExplorerResult
{
    public IReadOnlyList<RegistryRecordRow> Items { get; set; } = [];
    public bool HasNextPage { get; set; }
    public string? NextCursorName { get; set; }
    public string? NextCursorIdentifier { get; set; }
    public string? ValidationErrorMessage { get; set; }
    public bool SearchExecuted { get; set; }
    public RegistryExplorerCriteria Criteria { get; set; } = new();
}

public sealed class RegistryDashboardViewModel
{
    public RegistrySnapshotState State { get; set; }
    public RegistryAggregateData? Aggregates { get; set; }
    public RegistryExplorerResult Explorer { get; set; } = new();
    public string ActiveTab { get; set; } = "overview"; // overview, companies, llps, foreign, explorer
    public string? StatusMessage { get; set; }
}

