namespace MCAROC_Analysis.Services.McaFilings;

using MCAROC_Analysis.Services.Storage;

/// <summary>Storage layout per the plan: App_Data/Requests/{RequestId}/mca-filings/{BatchId}/{FilingId}/
/// (documents/, text/, temp/) — a second App_Data root alongside Phase 1's App_Data/Uploads/, kept
/// separate since this pipeline's per-filing/per-document volume is much higher.</summary>
public static class FilingStoragePaths
{
    private static IStoragePathResolver Resolver(string contentRootPath) =>
        !string.IsNullOrWhiteSpace(contentRootPath) ? new StoragePathResolver(Path.Combine(contentRootPath, "App_Data")) : StoragePathHelper.Default;

    public static string BatchRoot(string contentRootPath, long requestId, long batchId) =>
        Resolver(contentRootPath).GetMcaFilingsBatchDir(requestId, batchId);

    public static string FilingRoot(string contentRootPath, long requestId, long batchId, long filingId) =>
        Path.Combine(BatchRoot(contentRootPath, requestId, batchId), filingId.ToString());

    public static string DocumentsDir(string contentRootPath, long requestId, long batchId, long filingId) =>
        Resolver(contentRootPath).GetMcaFilingsDocumentsDir(requestId, batchId, filingId);

    public static string TextDir(string contentRootPath, long requestId, long batchId, long filingId) =>
        Resolver(contentRootPath).GetMcaFilingsTextDir(requestId, batchId, filingId);

    public static string TempDir(string contentRootPath, long requestId, long batchId, long filingId) =>
        Path.Combine(FilingRoot(contentRootPath, requestId, batchId, filingId), "temp");

    public static string BatchTempDir(string contentRootPath, long requestId, long batchId) =>
        Path.Combine(BatchRoot(contentRootPath, requestId, batchId), "temp");
}
