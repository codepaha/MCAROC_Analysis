using System;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace MCAROC_Analysis.Services.Registry;

public sealed class RegistryPromotionCoordinator : IRegistryPromotionCoordinator
{
    private const string LockResource = "CompanyMaster_PromotionAdmission";

    private readonly string _connectionString;
    private readonly ILogger<RegistryPromotionCoordinator>? _logger;
    private readonly SemaphoreSlim _inProcessGate = new(1, 1);
    private readonly object _stateLock = new();
    private long? _activePromotionJobId;

    public RegistryPromotionCoordinator(
        IConfiguration configuration,
        ILogger<RegistryPromotionCoordinator>? logger = null)
    {
        _connectionString = configuration.GetConnectionString("Default")
            ?? configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("Connection string 'Default' or 'DefaultConnection' is required.");
        _logger = logger;
    }

    public RegistryPromotionCoordinator(
        string connectionString,
        ILogger<RegistryPromotionCoordinator>? logger = null)
    {
        _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
        _logger = logger;
    }

    public bool IsPromotionActive(out long? activeJobId)
    {
        lock (_stateLock)
        {
            activeJobId = _activePromotionJobId;
            return _activePromotionJobId.HasValue;
        }
    }

    public async Task<IAsyncDisposable> AcquirePromotionAdmissionAsync(
        long syncRunId,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        int timeoutMs = Math.Max(0, (int)timeout.TotalMilliseconds);

        // 1. In-process admission gate
        bool enteredLocal = await _inProcessGate.WaitAsync(timeoutMs, cancellationToken);
        if (!enteredLocal)
        {
            throw new TimeoutException($"Timed out after {timeout.TotalSeconds:N1}s waiting for local promotion admission gate for Job {syncRunId}.");
        }

        lock (_stateLock)
        {
            _activePromotionJobId = syncRunId;
        }

        // 2. Distributed SQL Server session applock
        SqlConnection? connection = null;
        try
        {
            connection = new SqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            await using var cmd = new SqlCommand("sp_getapplock", connection)
            {
                CommandType = CommandType.StoredProcedure,
                CommandTimeout = Math.Max(30, (timeoutMs / 1000) + 10)
            };

            cmd.Parameters.AddWithValue("@Resource", LockResource);
            cmd.Parameters.AddWithValue("@LockMode", "Exclusive");
            cmd.Parameters.AddWithValue("@LockOwner", "Session");
            cmd.Parameters.AddWithValue("@LockTimeout", timeoutMs);

            var retParam = cmd.Parameters.Add("@ReturnValue", SqlDbType.Int);
            retParam.Direction = ParameterDirection.ReturnValue;

            await cmd.ExecuteNonQueryAsync(cancellationToken);
            int returnCode = (int)retParam.Value;

            if (returnCode < 0)
            {
                throw new TimeoutException($"Failed to acquire exclusive SQL applock '{LockResource}' for Job {syncRunId} (return code {returnCode}).");
            }

            _logger?.LogInformation("Acquired exclusive promotion admission for Job {JobId} on SPID {Spid}",
                syncRunId, connection.ServerProcessId);

            return new PromotionAdmissionScope(this, connection, syncRunId);
        }
        catch
        {
            lock (_stateLock)
            {
                _activePromotionJobId = null;
            }
            if (connection != null)
            {
                await connection.DisposeAsync();
            }
            _inProcessGate.Release();
            throw;
        }
    }

    public async Task<IAsyncDisposable?> TryAcquireRebuildGateAsync(CancellationToken cancellationToken = default)
    {
        lock (_stateLock)
        {
            if (_activePromotionJobId.HasValue)
            {
                return null;
            }
        }

        // 1. In-process gate check with 0 timeout
        bool enteredLocal = await _inProcessGate.WaitAsync(0, cancellationToken);
        if (!enteredLocal)
        {
            return null;
        }

        lock (_stateLock)
        {
            if (_activePromotionJobId.HasValue)
            {
                _inProcessGate.Release();
                return null;
            }
        }

        // 2. Distributed SQL Server shared lock probe with 0 timeout
        SqlConnection? connection = null;
        try
        {
            connection = new SqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            await using var cmd = new SqlCommand("sp_getapplock", connection)
            {
                CommandType = CommandType.StoredProcedure,
                CommandTimeout = 30
            };

            cmd.Parameters.AddWithValue("@Resource", LockResource);
            cmd.Parameters.AddWithValue("@LockMode", "Shared");
            cmd.Parameters.AddWithValue("@LockOwner", "Session");
            cmd.Parameters.AddWithValue("@LockTimeout", 0);

            var retParam = cmd.Parameters.Add("@ReturnValue", SqlDbType.Int);
            retParam.Direction = ParameterDirection.ReturnValue;

            await cmd.ExecuteNonQueryAsync(cancellationToken);
            int returnCode = (int)retParam.Value;

            if (returnCode < 0)
            {
                // Lock held exclusively by an active promotion node
                await connection.DisposeAsync();
                _inProcessGate.Release();
                return null;
            }

            _logger?.LogDebug("Acquired shared rebuild gate on SPID {Spid}", connection.ServerProcessId);
            return new RebuildGateScope(this, connection);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Exception probing shared rebuild gate for '{LockResource}'", LockResource);
            if (connection != null)
            {
                await connection.DisposeAsync();
            }
            _inProcessGate.Release();
            return null;
        }
    }

    private async Task ReleasePromotionAsync(SqlConnection connection, long syncRunId)
    {
        try
        {
            await using var cmd = new SqlCommand("sp_releaseapplock", connection)
            {
                CommandType = CommandType.StoredProcedure,
                CommandTimeout = 30
            };
            cmd.Parameters.AddWithValue("@Resource", LockResource);
            cmd.Parameters.AddWithValue("@LockOwner", "Session");
            await cmd.ExecuteNonQueryAsync();
            _logger?.LogInformation("Released exclusive promotion admission for Job {JobId}", syncRunId);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Error releasing promotion admission lock for Job {JobId}", syncRunId);
        }
        finally
        {
            await connection.DisposeAsync();
            lock (_stateLock)
            {
                if (_activePromotionJobId == syncRunId)
                {
                    _activePromotionJobId = null;
                }
            }
            _inProcessGate.Release();
        }
    }

    private async Task ReleaseRebuildAsync(SqlConnection connection)
    {
        try
        {
            await using var cmd = new SqlCommand("sp_releaseapplock", connection)
            {
                CommandType = CommandType.StoredProcedure,
                CommandTimeout = 30
            };
            cmd.Parameters.AddWithValue("@Resource", LockResource);
            cmd.Parameters.AddWithValue("@LockOwner", "Session");
            await cmd.ExecuteNonQueryAsync();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Error releasing shared rebuild gate lock for '{LockResource}'", LockResource);
        }
        finally
        {
            await connection.DisposeAsync();
            _inProcessGate.Release();
        }
    }

    private sealed class PromotionAdmissionScope : IAsyncDisposable
    {
        private readonly RegistryPromotionCoordinator _parent;
        private readonly SqlConnection _connection;
        private readonly long _jobId;
        private int _disposed;

        public PromotionAdmissionScope(RegistryPromotionCoordinator parent, SqlConnection connection, long jobId)
        {
            _parent = parent;
            _connection = connection;
            _jobId = jobId;
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                await _parent.ReleasePromotionAsync(_connection, _jobId);
            }
        }
    }

    private sealed class RebuildGateScope : IAsyncDisposable
    {
        private readonly RegistryPromotionCoordinator _parent;
        private readonly SqlConnection _connection;
        private int _disposed;

        public RebuildGateScope(RegistryPromotionCoordinator parent, SqlConnection connection)
        {
            _parent = parent;
            _connection = connection;
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                await _parent.ReleaseRebuildAsync(_connection);
            }
        }
    }
}
