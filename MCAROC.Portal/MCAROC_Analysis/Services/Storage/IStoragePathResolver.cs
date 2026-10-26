namespace MCAROC_Analysis.Services.Storage;

public interface IStoragePathResolver
{
    /// <summary>
    /// Gets the absolute path of the application data root (e.g. App_Data or configured Storage:Root).
    /// </summary>
    string GetDataRoot();

    /// <summary>
    /// Converts a full or legacy path to a path relative to the data root.
    /// If the path contains "App_Data", extracts the relative path following "App_Data/".
    /// If the path is already relative, normalizes it.
    /// If the path is absolute outside the data root and does not contain App_Data, leaves it unchanged.
    /// </summary>
    string ToRelativePath(string? path);

    /// <summary>
    /// Resolves a stored path (relative or legacy absolute) to a full path under the active data root.
    /// If the path is relative, it is combined with the active data root.
    /// If the path is legacy absolute containing "App_Data", it is remapped to sit under the active data root.
    /// </summary>
    string ResolveFullPath(string? path);

    /// <summary>
    /// Verifies that candidateFullPath sits securely inside expectedDirectory, preventing path traversal.
    /// </summary>
    bool IsContained(string candidateFullPath, string expectedDirectory);

    string GetUploadsDir(long requestId);
    string GetDerivativesDir(long requestId);
    string GetLitigationOrdersDir(long requestId);
    string GetMcaFilingsBatchDir(long requestId, long batchId);
    string GetMcaFilingsDocumentsDir(long requestId, long batchId, long filingId);
    string GetMcaFilingsTextDir(long requestId, long batchId, long filingId);
    string GetPreLoginReportsDir();
}
