namespace MCAROC_Analysis.Services.Storage;

public static class StoragePathHelper
{
    private static IStoragePathResolver? _defaultResolver;

    public static void Initialize(IStoragePathResolver resolver) => _defaultResolver = resolver;

    public static IStoragePathResolver Default => _defaultResolver ??= new StoragePathResolver();

    public static string GetDataRoot() => Default.GetDataRoot();
    public static string ToRelativePath(string? path) => Default.ToRelativePath(path);
    public static string ResolveFullPath(string? path) => Default.ResolveFullPath(path);
    public static bool IsContained(string candidateFullPath, string expectedDirectory) => Default.IsContained(candidateFullPath, expectedDirectory);

    public static string GetUploadsDir(long requestId) => Default.GetUploadsDir(requestId);
    public static string GetDerivativesDir(long requestId) => Default.GetDerivativesDir(requestId);
    public static string GetLitigationOrdersDir(long requestId) => Default.GetLitigationOrdersDir(requestId);
    public static string GetMcaFilingsBatchDir(long requestId, long batchId) => Default.GetMcaFilingsBatchDir(requestId, batchId);
    public static string GetMcaFilingsDocumentsDir(long requestId, long batchId, long filingId) => Default.GetMcaFilingsDocumentsDir(requestId, batchId, filingId);
    public static string GetMcaFilingsTextDir(long requestId, long batchId, long filingId) => Default.GetMcaFilingsTextDir(requestId, batchId, filingId);
    public static string GetPreLoginReportsDir() => Default.GetPreLoginReportsDir();
}
