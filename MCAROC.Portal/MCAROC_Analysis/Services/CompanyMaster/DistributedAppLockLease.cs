using System;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace MCAROC_Analysis.Services.CompanyMaster;

public sealed class DistributedAppLockLease : ISyncLockLease
{
    private readonly string _connectionString;
    private readonly ILogger<DistributedAppLockLease> _logger;
    private SqlConnection? _connection;
    private string? _acquiredResource;
    private bool _isDisposed;

    public bool IsAcquired => _acquiredResource != null && _connection?.State == ConnectionState.Open;
    public int? ActiveSpid => _connection?.State == ConnectionState.Open ? _connection.ServerProcessId : null;

    public DistributedAppLockLease(IConfiguration configuration, ILogger<DistributedAppLockLease> logger)
    {
        _connectionString = configuration.GetConnectionString("DefaultConnection") 
            ?? throw new InvalidOperationException("DefaultConnection string is not configured.");
        _logger = logger;
    }

    public DistributedAppLockLease(string connectionString, ILogger<DistributedAppLockLease> logger)
    {
        _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
        _logger = logger;
    }

    public async Task<LockAcquisitionResult> TryAcquireAsync(string lockResource, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(lockResource))
            throw new ArgumentException("Lock resource cannot be null or empty.", nameof(lockResource));

        if (IsAcquired)
            return LockAcquisitionResult.Success(0);

        _connection = new SqlConnection(_connectionString);
        await _connection.OpenAsync(cancellationToken);

        int timeoutMs = Math.Max(0, (int)timeout.TotalMilliseconds);

        await using var cmd = new SqlCommand("sp_getapplock", _connection)
        {
            CommandType = CommandType.StoredProcedure,
            CommandTimeout = Math.Max(30, (timeoutMs / 1000) + 10)
        };

        cmd.Parameters.AddWithValue("@Resource", lockResource);
        cmd.Parameters.AddWithValue("@LockMode", "Exclusive");
        cmd.Parameters.AddWithValue("@LockOwner", "Session");
        cmd.Parameters.AddWithValue("@LockTimeout", timeoutMs);

        var returnParam = cmd.Parameters.Add("@ReturnValue", SqlDbType.Int);
        returnParam.Direction = ParameterDirection.ReturnValue;

        try
        {
            await cmd.ExecuteNonQueryAsync(cancellationToken);
            int returnCode = (int)returnParam.Value;

            if (returnCode >= 0)
            {
                _acquiredResource = lockResource;
                _logger.LogInformation("Acquired exclusive session applock '{LockResource}' (SPID {Spid}, returnCode: {Code})",
                    lockResource, _connection.ServerProcessId, returnCode);
                return LockAcquisitionResult.Success(returnCode);
            }

            int? blockingSpid = null;
            string reason = returnCode switch
            {
                -1 => "Lock request timed out (already held by another session).",
                -2 => "Lock request was canceled.",
                -3 => "Lock request was chosen as a deadlock victim.",
                -999 => "Parameter validation or internal SQL Server lock error.",
                _ => $"Lock acquisition failed with return code {returnCode}."
            };

            if (returnCode == -1)
            {
                blockingSpid = await DiscoverBlockingSpidAsync(lockResource, cancellationToken);
                if (blockingSpid.HasValue)
                {
                    reason += $" Held by active SPID {blockingSpid.Value}.";
                }
            }

            _logger.LogWarning("Failed to acquire session applock '{LockResource}': {Reason} (returnCode: {Code})",
                lockResource, reason, returnCode);

            await CleanupConnectionAsync();
            return LockAcquisitionResult.Failed(returnCode, reason, blockingSpid);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Unexpected exception acquiring session applock '{LockResource}'", lockResource);
            await CleanupConnectionAsync();
            throw;
        }
    }

    private async Task<int?> DiscoverBlockingSpidAsync(string lockResource, CancellationToken cancellationToken)
    {
        try
        {
            if (_connection == null || _connection.State != ConnectionState.Open) return null;

            const string sql = @"
                SELECT TOP (1) request_session_id 
                FROM sys.dm_tran_locks 
                WHERE resource_type = 'APPLICATION' 
                  AND resource_description LIKE '%' + @Resource + '%'
                  AND request_mode = 'X'
                  AND request_status = 'GRANT'
                  AND request_session_id <> @@SPID;";

            await using var cmd = new SqlCommand(sql, _connection);
            cmd.Parameters.AddWithValue("@Resource", lockResource);
            var result = await cmd.ExecuteScalarAsync(cancellationToken);
            if (result != null && result != DBNull.Value && int.TryParse(result.ToString(), out int spid))
            {
                return spid;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to query sys.dm_tran_locks for blocking SPID.");
        }
        return null;
    }

    public async Task<bool> TryExtendHeartbeatAsync(long jobId, long fencingToken, TimeSpan extension, CancellationToken cancellationToken = default)
    {
        if (_connection == null || _connection.State != ConnectionState.Open)
            return false;

        try
        {
            const string sql = @"
                UPDATE dbo.CompanyMasterSyncJobs
                SET LastHeartbeatUtc = SYSUTCDATETIME(),
                    LeaseExpiresUtc = DATEADD(second, @Seconds, SYSUTCDATETIME())
                WHERE JobId = @JobId
                  AND FencingToken = @FencingToken
                  AND Status IN ('Probing', 'Downloading', 'Staging');";

            await using var cmd = new SqlCommand(sql, _connection);
            cmd.Parameters.AddWithValue("@JobId", jobId);
            cmd.Parameters.AddWithValue("@FencingToken", fencingToken);
            cmd.Parameters.AddWithValue("@Seconds", (int)extension.TotalSeconds);

            int affected = await cmd.ExecuteNonQueryAsync(cancellationToken);
            if (affected == 0)
            {
                _logger.LogWarning("Heartbeat update affected 0 rows for Job {JobId} with Token {FencingToken}. Preemption detected.",
                    jobId, fencingToken);
                return false;
            }

            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to extend heartbeat for Job {JobId}", jobId);
            return false;
        }
    }

    public async Task ReleaseAsync(CancellationToken cancellationToken = default)
    {
        if (_acquiredResource == null || _connection == null || _connection.State != ConnectionState.Open)
        {
            await CleanupConnectionAsync();
            return;
        }

        try
        {
            await using var cmd = new SqlCommand("sp_releaseapplock", _connection)
            {
                CommandType = CommandType.StoredProcedure
            };
            cmd.Parameters.AddWithValue("@Resource", _acquiredResource);
            cmd.Parameters.AddWithValue("@LockOwner", "Session");

            await cmd.ExecuteNonQueryAsync(cancellationToken);
            _logger.LogInformation("Released session applock '{LockResource}' (SPID {Spid})",
                _acquiredResource, _connection.ServerProcessId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Error releasing session applock '{LockResource}'", _acquiredResource);
        }
        finally
        {
            _acquiredResource = null;
            await CleanupConnectionAsync();
        }
    }

    private async Task CleanupConnectionAsync()
    {
        if (_connection != null)
        {
            try
            {
                await _connection.CloseAsync();
                await _connection.DisposeAsync();
            }
            catch
            {
                // ignore on dispose
            }
            finally
            {
                _connection = null;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        await ReleaseAsync(CancellationToken.None);
    }
}
