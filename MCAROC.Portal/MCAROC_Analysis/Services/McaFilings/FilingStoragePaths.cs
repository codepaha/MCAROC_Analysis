namespace MCAROC_Analysis.Services.McaFilings;

using MCAROC_Analysis.Services.Storage;

/// <summary>Storage layout per the plan: App_Data/Requests/{RequestId}/mca-filings/{BatchId}/{FilingId}/
/// (documents/, text/, temp/) — a second App_Data root alongside Phase 1's App_Data/Uploads/, kept
/// separate since this pipeline's per-filing/per-document volume is much higher.</summary>
public static class FilingStoragePaths
{
    private static IStoragePathResolver Resolve(IStoragePathResolver? resolver) =>
        resolver ?? StoragePathHelper.Default;

    public static string BatchRoot(long requestId, long batchId, IStoragePathResolver? resolver = null) =>
        Resolve(resolver).GetMcaFilingsBatchDir(requestId, batchId);

    public static string BatchRoot(string? contentRootPath, long requestId, long batchId) =>
        BatchRoot(requestId, batchId, null);

    public static string FilingRoot(long requestId, long batchId, long filingId, IStoragePathResolver? resolver = null) =>
        Path.Combine(BatchRoot(requestId, batchId, resolver), filingId.ToString());

    public static string FilingRoot(string? contentRootPath, long requestId, long batchId, long filingId) =>
        FilingRoot(requestId, batchId, filingId, null);

    public static string DocumentsDir(long requestId, long batchId, long filingId, IStoragePathResolver? resolver = null) =>
        Resolve(resolver).GetMcaFilingsDocumentsDir(requestId, batchId, filingId);

    public static string DocumentsDir(string? contentRootPath, long requestId, long batchId, long filingId) =>
        DocumentsDir(requestId, batchId, filingId, null);

    public static string TextDir(long requestId, long batchId, long filingId, IStoragePathResolver? resolver = null) =>
        Resolve(resolver).GetMcaFilingsTextDir(requestId, batchId, filingId);

    public static string TextDir(string? contentRootPath, long requestId, long batchId, long filingId) =>
        TextDir(requestId, batchId, filingId, null);

    public static string TempDir(long requestId, long batchId, long filingId, IStoragePathResolver? resolver = null) =>
        Path.Combine(FilingRoot(requestId, batchId, filingId, resolver), "temp");

    public static string TempDir(string? contentRootPath, long requestId, long batchId, long filingId) =>
        TempDir(requestId, batchId, filingId, null);

    public static string BatchTempDir(long requestId, long batchId, IStoragePathResolver? resolver = null) =>
        Path.Combine(BatchRoot(requestId, batchId, resolver), "temp");

    public static string BatchTempDir(string? contentRootPath, long requestId, long batchId) =>
        BatchTempDir(requestId, batchId, null);
}
