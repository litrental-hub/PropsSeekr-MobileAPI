namespace PropSeekr.Services;

public static class LocalBulkImportStorage
{
    public const string StorageKeyPrefix = "local/";
    private const string DefaultDirectoryName = "local-bulk-imports";

    public static bool IsLocalKey(string storageKey) =>
        storageKey.StartsWith(StorageKeyPrefix, StringComparison.Ordinal);

    public static string GetDirectory(IConfiguration configuration, IHostEnvironment environment)
    {
        var configuredPath = configuration["BulkImports:LocalStoragePath"];
        return string.IsNullOrWhiteSpace(configuredPath)
            ? Path.Combine(environment.ContentRootPath, DefaultDirectoryName)
            : Path.GetFullPath(configuredPath, environment.ContentRootPath);
    }

    public static string GetInputPath(
        Guid jobId,
        IConfiguration configuration,
        IHostEnvironment environment) =>
        Path.Combine(GetDirectory(configuration, environment), $"{jobId:N}.txt");

    public static string GetOutputPath(
        Guid jobId,
        IConfiguration configuration,
        IHostEnvironment environment) =>
        Path.Combine(GetDirectory(configuration, environment), $"{jobId:N}_listings.json");
}
