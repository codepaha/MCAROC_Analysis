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

    private const string RebuildLockResource = "CompanyMaster_AggregateRebuild";

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

        // 2. Distributed SQL Server dual-lock acquisition:
        //    - Shared lock on LockResource ("CompanyMaster_PromotionAdmission") ensures no active promotion
        //    - Exclusive lock on RebuildLockResource ("CompanyMaster_AggregateRebuild") ensures single rebuild owner across cluster
        SqlConnection? connection = null;
        bool acquiredSharedPromotion = false;
        try
        {
            connection = new SqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            // Step A: Acquire Shared lock on LockResource (timeout = 0)
            await using (var cmdProm = new SqlCommand("sp_getapplock", connection)
            {
                CommandType = CommandType.StoredProcedure,
                CommandTimeout = 30
            })
            {
                cmdProm.Parameters.AddWithValue("@Resource", LockResource);
                cmdProm.Parameters.AddWithValue("@LockMode", "Shared");
                cmdProm.Parameters.AddWithValue("@LockOwner", "Session");
                cmdProm.Parameters.AddWithValue("@LockTimeout", 0);

                var retParam = cmdProm.Parameters.Add("@ReturnValue", SqlDbType.Int);
                retParam.Direction = ParameterDirection.ReturnValue;

                await cmdProm.ExecuteNonQueryAsync(cancellationToken);
                int returnCode = (int)retParam.Value;

                if (returnCode < 0)
                {
                    // Active promotion in progress
                    await connection.DisposeAsync();
                    _inProcessGate.Release();
                    return null;
                }
                acquiredSharedPromotion = true;
            }

            // Step B: Acquire Exclusive lock on RebuildLockResource (timeout = 0)
            await using (var cmdReb = new SqlCommand("sp_getapplock", connection)
            {
                CommandType = CommandType.StoredProcedure,
                CommandTimeout = 30
            })
            {
                cmdReb.Parameters.AddWithValue("@Resource", RebuildLockResource);
                cmdReb.Parameters.AddWithValue("@LockMode", "Exclusive");
                cmdReb.Parameters.AddWithValue("@LockOwner", "Session");
                cmdReb.Parameters.AddWithValue("@LockTimeout", 0);

                var retParam = cmdReb.Parameters.Add("@ReturnValue", SqlDbType.Int);
                retParam.Direction = ParameterDirection.ReturnValue;

                await cmdReb.ExecuteNonQueryAsync(cancellationToken);
                int returnCode = (int)retParam.Value;

                if (returnCode < 0)
                {
                    // Another node is actively rebuilding aggregates across cluster
                    _logger?.LogInformation("Another portal node holds '{RebuildResource}'. Non-owner node backing off without scanning.", RebuildLockResource);
                    await ReleaseAppLockAsync(connection, LockResource);
                    await connection.DisposeAsync();
                    _inProcessGate.Release();
                    return null;
                }
            }

            _logger?.LogDebug("Acquired shared promotion gate and exclusive rebuild gate on SPID {Spid}", connection.ServerProcessId);
            return new RebuildGateScope(this, connection);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Exception acquiring rebuild gate locks");
            if (connection != null)
            {
                if (acquiredSharedPromotion)
                {
                    await ReleaseAppLockAsync(connection, LockResource);
                }
                await connection.DisposeAsync();
            }
            _inProcessGate.Release();
            return null;
        }
    }

    public async Task<bool> TryProbePromotionAdmissionAsync(CancellationToken cancellationToken = default)
    {
        lock (_stateLock)
        {
            if (_activePromotionJobId.HasValue)
            {
                return false;
            }
        }

        try
        {
            await using var connection = new SqlConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            await using var cmd = new SqlCommand("sp_getapplock", connection)
            {
                CommandType = CommandType.StoredProcedure,
                CommandTimeout = 10
            };

            cmd.Parameters.AddWithValue("@Resource", LockResource);
            cmd.Parameters.AddWithValue("@LockMode", "Shared");
            cmd.Parameters.AddWithValue("@LockOwner", "Session");
            cmd.Parameters.AddWithValue("@LockTimeout", 0);

            var retParam = cmd.Parameters.Add("@ReturnValue", SqlDbType.Int);
            retParam.Direction = ParameterDirection.ReturnValue;

            await cmd.ExecuteNonQueryAsync(cancellationToken);
            int returnCode = (int)retParam.Value;

            if (returnCode >= 0)
            {
                await ReleaseAppLockAsync(connection, LockResource);
                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Advisory promotion gate probe observed exception. Treating as locked.");
            return false;
        }
    }

    private static async Task ReleaseAppLockAsync(SqlConnection connection, string resource)
    {
        try
        {
            await using var cmd = new SqlCommand("sp_releaseapplock", connection)
            {
                CommandType = CommandType.StoredProcedure,
                CommandTimeout = 10
            };
            cmd.Parameters.AddWithValue("@Resource", resource);
            cmd.Parameters.AddWithValue("@LockOwner", "Session");
            await cmd.ExecuteNonQueryAsync();
        }
        catch
        {
            // Best effort release on abort/cleanup
        }
    }

    private async Task ReleasePromotionAsync(SqlConnection connection, long syncRunId)
    {
        try
        {
            await ReleaseAppLockAsync(connection, LockResource);
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
            await ReleaseAppLockAsync(connection, RebuildLockResource);
            await ReleaseAppLockAsync(connection, LockResource);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Error releasing rebuild gate locks");
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

