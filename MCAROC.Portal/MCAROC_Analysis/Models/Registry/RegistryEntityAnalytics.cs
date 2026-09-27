using MCAROC_Analysis.Data.Entities;

namespace MCAROC_Analysis.Models.Registry;

public sealed record RegistryMetricBucket(string Label, int Count);
public sealed record RegistryRegistrationPeriod(int Year, int Month, int Count);
public sealed record RegistryStateStatus(string State, string Status, int Count);

/// <summary>Bounded server aggregates, never the underlying master records.</summary>
public sealed class RegistryEntityAnalytics
{
    public CompanyMasterRecordType RecordType { get; set; }
    public List<RegistryMetricBucket> Statuses { get; set; } = [];
    public List<RegistryMetricBucket> States { get; set; } = [];
    public List<RegistryMetricBucket> Industries { get; set; } = [];
    public List<RegistryMetricBucket> Rocs { get; set; } = [];
    public List<RegistryMetricBucket> Districts { get; set; } = [];
    public List<RegistryMetricBucket> DistressedStates { get; set; } = [];
    public List<RegistryStateStatus> StateStatuses { get; set; } = [];
    public List<RegistryRegistrationPeriod> Registrations { get; set; } = [];
    public int Total => Statuses.Sum(s => s.Count);
    public int Distressed => Statuses.Where(s => IsDistressed(s.Label)).Sum(s => s.Count);
    public static bool IsDistressed(string? status) => status?.Trim().ToUpperInvariant() is
        "UNDER CIRP" or "UNDER LIQUIDATION" or "DISSOLVED (LIQUIDATED)" or
        "DISSOLVED UNDER SECTION 54" or "DISSOLVED UNDER SECTION 59(8)" or
        "UNDER PROCESS OF STRIKE-OFF";
}
