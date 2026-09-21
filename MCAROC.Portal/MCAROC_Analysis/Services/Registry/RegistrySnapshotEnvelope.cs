using System;

namespace MCAROC_Analysis.Services.Registry;

/// <summary>
/// Versioned and integrity-hashed envelope for persisted registry aggregate snapshots.
/// </summary>
public sealed class RegistrySnapshotEnvelope
{
    public const int CurrentVersion = 1;

    public int PayloadVersion { get; set; } = CurrentVersion;
    public long JobId { get; set; }
    public DateOnly? PublishedDate { get; set; }
    public DateTime? CompletedUtc { get; set; }
    public string Source { get; set; } = string.Empty;
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Hex-encoded SHA256 hash computed directly over the exact canonical UTF-8 bytes of <see cref="RawDtoJson"/>.
    /// </summary>
    public string Sha256Hash { get; set; } = string.Empty;

    /// <summary>
    /// Canonical JSON string representing the serialized <see cref="RegistryAggregateSnapshotDto"/>.
    /// </summary>
    public string RawDtoJson { get; set; } = string.Empty;
}
