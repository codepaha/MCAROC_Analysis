namespace MCAROC_Analysis.Services.Storage;

using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Options;

public sealed class StoragePathResolver : IStoragePathResolver
{
    private readonly string _dataRoot;

    public StoragePathResolver(Microsoft.Extensions.Hosting.IHostEnvironment? env = null, IOptions<StorageOptions>? options = null)
    {
        var configuredRoot = options?.Value?.Root;
        if (!string.IsNullOrWhiteSpace(configuredRoot))
        {
            _dataRoot = Path.IsPathRooted(configuredRoot)
                ? Path.GetFullPath(configuredRoot)
                : Path.GetFullPath(Path.Combine(env?.ContentRootPath ?? Directory.GetCurrentDirectory(), configuredRoot));
        }
        else
        {
            var baseDir = env?.ContentRootPath ?? Directory.GetCurrentDirectory();
            _dataRoot = Path.GetFullPath(Path.Combine(baseDir, "App_Data"));
        }
    }

    public StoragePathResolver(string dataRoot)
    {
        _dataRoot = Path.GetFullPath(dataRoot);
    }

    public string GetDataRoot() => _dataRoot;

    private static bool IsRootedOrDrive(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (Path.IsPathRooted(path)) return true;
        if (path.Length >= 3 && char.IsLetter(path[0]) && path[1] == ':' && (path[2] == '\\' || path[2] == '/'))
            return true;
        return false;
    }

    public string ToRelativePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return string.Empty;

        var normalized = path.Replace('\\', '/');

        // Check for App_Data marker anywhere in the path
        const string appDataMarker = "/app_data/";
        var markerIdx = normalized.IndexOf(appDataMarker, StringComparison.OrdinalIgnoreCase);
        if (markerIdx >= 0)
        {
            return normalized.Substring(markerIdx + appDataMarker.Length).TrimStart('/');
        }

        if (normalized.StartsWith("app_data/", StringComparison.OrdinalIgnoreCase))
        {
            return normalized.Substring("app_data/".Length).TrimStart('/');
        }

        // If path is rooted or has a Windows drive prefix
        if (IsRootedOrDrive(path))
        {
            var isFullyQualified = Path.IsPathFullyQualified(path) || (path.Length >= 3 && char.IsLetter(path[0]) && path[1] == ':');

            // On Windows, paths like "\Requests\..." have no drive letter (Path.IsPathFullyQualified is false)
            // and should be treated as relative paths with a leading separator.
            if (!isFullyQualified)
            {
                return normalized.TrimStart('/');
            }

            var fullRoot = _dataRoot;
            if (!fullRoot.EndsWith(Path.DirectorySeparatorChar) && !fullRoot.EndsWith(Path.AltDirectorySeparatorChar))
                fullRoot += Path.DirectorySeparatorChar;

            // Only attempt Path.GetFullPath if it's native rooted on this OS
            if (Path.IsPathRooted(path))
            {
                var fullPath = Path.GetFullPath(path);
                if (fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
                {
                    return Path.GetRelativePath(_dataRoot, fullPath).Replace('\\', '/').TrimStart('/');
                }
            }

            // Path is absolute, but outside data root and doesn't contain App_Data: keep unchanged
            return path;
        }

        // Already relative; clean leading slashes and normalize to forward slashes
        return normalized.TrimStart('/');
    }

    public string ResolveFullPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return string.Empty;

        // If path is relative
        if (!IsRootedOrDrive(path))
        {
            var cleanRelative = path.Replace('/', Path.DirectorySeparatorChar)
                                    .Replace('\\', Path.DirectorySeparatorChar)
                                    .TrimStart(Path.DirectorySeparatorChar);
            return Path.GetFullPath(Path.Combine(_dataRoot, cleanRelative));
        }

        // If it's a legacy absolute path containing App_Data, remap to active data root
        var relative = ToRelativePath(path);
        if (!string.Equals(relative, path, StringComparison.OrdinalIgnoreCase))
        {
            var cleanRelative = relative.Replace('/', Path.DirectorySeparatorChar)
                                        .Replace('\\', Path.DirectorySeparatorChar)
                                        .TrimStart(Path.DirectorySeparatorChar);
            return Path.GetFullPath(Path.Combine(_dataRoot, cleanRelative));
        }

        // On non-Windows OS, a Windows drive path (e.g. D:\External\...) cannot be resolved via Path.GetFullPath
        if (!Path.IsPathRooted(path))
        {
            return path;
        }

        // Path is rooted (absolute).
        var fullPath = Path.GetFullPath(path);

        // Check if it's already under the active _dataRoot
        var fullRoot = _dataRoot;
        if (!fullRoot.EndsWith(Path.DirectorySeparatorChar) && !fullRoot.EndsWith(Path.AltDirectorySeparatorChar))
            fullRoot += Path.DirectorySeparatorChar;

        if (fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            return fullPath;
        }

        // Absolute path outside data root and outside App_Data
        return fullPath;
    }

    public bool IsContained(string candidateFullPath, string expectedDirectory)
    {
        if (string.IsNullOrWhiteSpace(candidateFullPath) || string.IsNullOrWhiteSpace(expectedDirectory))
            return false;

        var fullCandidate = Path.GetFullPath(candidateFullPath);
        var fullExpected = Path.GetFullPath(expectedDirectory);
        if (!fullExpected.EndsWith(Path.DirectorySeparatorChar) && !fullExpected.EndsWith(Path.AltDirectorySeparatorChar))
            fullExpected += Path.DirectorySeparatorChar;

        return fullCandidate.StartsWith(fullExpected, StringComparison.OrdinalIgnoreCase);
    }

    public string GetUploadsDir(long requestId) =>
        Path.Combine(_dataRoot, "Uploads", requestId.ToString(), "original");

    public string GetDerivativesDir(long requestId) =>
        Path.Combine(_dataRoot, "Uploads", requestId.ToString(), "derivatives");

    public string GetLitigationOrdersDir(long requestId) =>
        Path.Combine(_dataRoot, "Requests", requestId.ToString(), "litigation-orders");

    public string GetMcaFilingsBatchDir(long requestId, long batchId) =>
        Path.Combine(_dataRoot, "Requests", requestId.ToString(), "mca-filings", batchId.ToString());

    public string GetMcaFilingsDocumentsDir(long requestId, long batchId, long filingId) =>
        Path.Combine(GetMcaFilingsBatchDir(requestId, batchId), filingId.ToString(), "documents");

    public string GetMcaFilingsTextDir(long requestId, long batchId, long filingId) =>
        Path.Combine(GetMcaFilingsBatchDir(requestId, batchId), filingId.ToString(), "text");

    public string GetPreLoginReportsDir() =>
        Path.Combine(_dataRoot, "PreLoginReports");
}
