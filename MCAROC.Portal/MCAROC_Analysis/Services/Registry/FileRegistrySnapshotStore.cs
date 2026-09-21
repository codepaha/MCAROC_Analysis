using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MCAROC_Analysis.Models.Registry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MCAROC_Analysis.Services.Registry;

public sealed class FileRegistrySnapshotStore : IRegistrySnapshotStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    private readonly RegistrySnapshotStoreOptions _options;
    private readonly IHostEnvironment? _env;
    private readonly ILogger<FileRegistrySnapshotStore>? _logger;
    private readonly string _resolvedRoot;

    public FileRegistrySnapshotStore(
        IOptions<RegistrySnapshotStoreOptions> options,
        IHostEnvironment? env = null,
        ILogger<FileRegistrySnapshotStore>? logger = null)
    {
        _options = options?.Value ?? new RegistrySnapshotStoreOptions();
        _env = env;
        _logger = logger;
        _resolvedRoot = ResolveRootPath(_options, _env);
    }

    public string RootPath => _resolvedRoot;

    public static string ResolveRootPath(RegistrySnapshotStoreOptions options, IHostEnvironment? env)
    {
        bool isProduction = env?.IsProduction() == true;
        if (!string.IsNullOrWhiteSpace(options.Root))
        {
            return Path.GetFullPath(options.Root);
        }

        if (isProduction)
        {
            throw new InvalidOperationException(
                "RegistrySnapshotStore:Root must be explicitly configured to a shared, durable filesystem volume in production deployments to prevent multi-node table-scan fan-out.");
        }

        string baseDir = env?.ContentRootPath ?? AppContext.BaseDirectory;
        return Path.Combine(baseDir, "App_Data", "RegistrySnapshots");
    }

    public static void ValidatePreflight(IServiceProvider services, IHostEnvironment env)
    {
        var options = services.GetRequiredService<IOptions<RegistrySnapshotStoreOptions>>().Value;
        string root = ResolveRootPath(options, env);

        try
        {
            Directory.CreateDirectory(root);

            // Canary write test to verify write/delete permissions
            string canaryFile = Path.Combine(root, $".canary.{Guid.NewGuid():N}.tmp");
            File.WriteAllText(canaryFile, "canary");
            File.Delete(canaryFile);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"RegistrySnapshotStore preflight failed: configured root path '{root}' is inaccessible or lacks write permissions.", ex);
        }
    }

    public async Task SaveSnapshotAsync(long jobId, RegistryAggregateData data, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(data);

        Directory.CreateDirectory(_resolvedRoot);

        var dto = RegistryAggregateSnapshotDto.FromModel(data);
        byte[] dtoBytes = JsonSerializer.SerializeToUtf8Bytes(dto, JsonOptions);
        string sha256 = Convert.ToHexString(SHA256.HashData(dtoBytes));

        var envelope = new RegistrySnapshotEnvelope
        {
            PayloadVersion = RegistrySnapshotEnvelope.CurrentVersion,
            JobId = jobId,
            PublishedDate = data.Metadata.PublishedDate,
            CompletedUtc = data.Metadata.CompletedUtc,
            Source = data.Metadata.Source,
            CreatedUtc = DateTime.UtcNow,
            Sha256Hash = sha256,
            RawDtoJson = Encoding.UTF8.GetString(dtoBytes)
        };

        byte[] envelopeBytes = JsonSerializer.SerializeToUtf8Bytes(envelope, JsonOptions);

        // Unique temporary file on the same volume/directory ensures atomic rename
        string tempFile = Path.Combine(_resolvedRoot, $"snapshot_{jobId}.tmp.{Guid.NewGuid():N}");
        string targetFile = Path.Combine(_resolvedRoot, $"snapshot_{jobId}.json");

        await File.WriteAllBytesAsync(tempFile, envelopeBytes, ct);
        File.Move(tempFile, targetFile, overwrite: true);

        _logger?.LogInformation("Successfully persisted verified aggregate snapshot envelope for Job {JobId} at '{TargetFile}'",
            jobId, targetFile);

        PruneOldSnapshots(_resolvedRoot, _options.RetentionCount);
    }

    public async Task<RegistryAggregateData?> GetSnapshotAsync(long jobId, CancellationToken ct = default)
    {
        string targetFile = Path.Combine(_resolvedRoot, $"snapshot_{jobId}.json");
        if (!File.Exists(targetFile))
        {
            return null;
        }

        try
        {
            byte[] bytes = await File.ReadAllBytesAsync(targetFile, ct);
            var envelope = JsonSerializer.Deserialize<RegistrySnapshotEnvelope>(bytes, JsonOptions);

            if (envelope == null)
            {
                _logger?.LogWarning("Failed to deserialize snapshot envelope from '{TargetFile}'", targetFile);
                return null;
            }

            if (envelope.PayloadVersion != RegistrySnapshotEnvelope.CurrentVersion)
            {
                _logger?.LogWarning("Unsupported snapshot payload version {Version} in '{TargetFile}' (expected {ExpectedVersion})",
                    envelope.PayloadVersion, targetFile, RegistrySnapshotEnvelope.CurrentVersion);
                return null;
            }

            if (envelope.JobId != jobId)
            {
                _logger?.LogWarning("Snapshot envelope JobId mismatch in '{TargetFile}': found {FoundJobId}, expected {ExpectedJobId}",
                    targetFile, envelope.JobId, jobId);
                return null;
            }

            if (string.IsNullOrWhiteSpace(envelope.RawDtoJson) || string.IsNullOrWhiteSpace(envelope.Sha256Hash))
            {
                _logger?.LogWarning("Snapshot envelope missing payload or integrity hash in '{TargetFile}'", targetFile);
                return null;
            }

            // Bit-exact SHA256 validation over raw canonical UTF-8 bytes
            byte[] rawBytes = Encoding.UTF8.GetBytes(envelope.RawDtoJson);
            string computedHash = Convert.ToHexString(SHA256.HashData(rawBytes));

            if (!string.Equals(computedHash, envelope.Sha256Hash, StringComparison.OrdinalIgnoreCase))
            {
                _logger?.LogWarning("Snapshot envelope SHA256 checksum mismatch in '{TargetFile}'. Data may be tampered or corrupted.", targetFile);
                return null;
            }

            var dto = JsonSerializer.Deserialize<RegistryAggregateSnapshotDto>(rawBytes, JsonOptions);
            if (dto == null)
            {
                _logger?.LogWarning("Failed to deserialize DTO from snapshot envelope in '{TargetFile}'", targetFile);
                return null;
            }

            return dto.ToModel();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Error reading snapshot envelope from '{TargetFile}'. Treating L2 as absent.", targetFile);
            return null;
        }
    }

    private void PruneOldSnapshots(string root, int retentionCount)
    {
        if (retentionCount <= 0)
        {
            return;
        }

        try
        {
            var files = Directory.GetFiles(root, "snapshot_*.json")
                .Select(path =>
                {
                    string name = Path.GetFileNameWithoutExtension(path);
                    if (name.StartsWith("snapshot_", StringComparison.OrdinalIgnoreCase) &&
                        long.TryParse(name.AsSpan("snapshot_".Length), out long id))
                    {
                        return new { Path = path, JobId = id };
                    }
                    return null;
                })
                .Where(x => x != null)
                .OrderByDescending(x => x!.JobId)
                .ToList();

            var toDelete = files.Skip(retentionCount);
            foreach (var item in toDelete)
            {
                try
                {
                    File.Delete(item!.Path);
                    _logger?.LogInformation("Pruned old snapshot file '{Path}' for Job {JobId}", item.Path, item.JobId);
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Failed to prune snapshot file '{Path}'", item!.Path);
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to enumerate snapshot files for pruning in '{Root}'", root);
        }
    }
}
