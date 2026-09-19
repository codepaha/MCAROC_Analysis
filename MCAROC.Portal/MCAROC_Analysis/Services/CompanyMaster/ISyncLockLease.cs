using System;
using System.Threading;
using System.Threading.Tasks;

namespace MCAROC_Analysis.Services.CompanyMaster;

public record LockAcquisitionResult(bool Succeeded, int ReturnCode, string? FailureReason, int? BlockingSpid = null)
{
    public static LockAcquisitionResult Success(int returnCode) => new(true, returnCode, null);
    public static LockAcquisitionResult Failed(int returnCode, string reason, int? blockingSpid = null) =>
        new(false, returnCode, reason, blockingSpid);
}

public class LeasePreemptedException : Exception
{
    public long JobId { get; }
    public long FencingToken { get; }

    public LeasePreemptedException(long jobId, long fencingToken, string message) : base(message)
    {
        JobId = jobId;
        FencingToken = fencingToken;
    }
}

public interface ISyncLockLease : IDisposable, IAsyncDisposable
{
    bool IsAcquired { get; }
    int? ActiveSpid { get; }
    Task<LockAcquisitionResult> TryAcquireAsync(string lockResource, TimeSpan timeout, CancellationToken cancellationToken = default);
    Task<bool> TryExtendHeartbeatAsync(long jobId, long fencingToken, TimeSpan extension, CancellationToken cancellationToken = default);
    Task ReleaseAsync(CancellationToken cancellationToken = default);
}
