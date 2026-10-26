namespace MCAROC_Analysis.Services.Storage;

public sealed class StorageOptions
{
    public const string SectionName = "Storage";

    /// <summary>
    /// Configurable root directory for application data storage.
    /// If null, empty, or whitespace, defaults to "App_Data" under the content root path.
    /// May be absolute or relative to the content root path.
    /// </summary>
    public string? Root { get; set; }
}
