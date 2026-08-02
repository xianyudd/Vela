namespace Vela.Windows.Diagnostics;

public sealed class AppPaths
{
    public AppPaths(string rootDirectory)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory) || !Path.IsPathFullyQualified(rootDirectory))
        {
            throw new ArgumentException("The application root directory must be an absolute path.", nameof(rootDirectory));
        }

        RootDirectory = Path.GetFullPath(rootDirectory);
    }

    public string RootDirectory { get; }

    public string ConfigurationFilePath => Path.Combine(RootDirectory, "config.json");

    public string ConfigurationTemporaryFilePath => Path.Combine(RootDirectory, "config.json.tmp");

    public string PendingDirectoryPath => Path.Combine(RootDirectory, "pending");

    public string LogsDirectoryPath => Path.Combine(RootDirectory, "logs");

    public static AppPaths CreateDefault() =>
        new(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Vela"));

    public string GetEventsFilePath(Guid runId) =>
        Path.Combine(GetRunDirectory(runId), "events.ndjson");

    public string GetPendingRequestFilePath(Guid runId) =>
        Path.Combine(PendingDirectoryPath, $"{FormatRunId(runId)}.json");

    public string GetRunDirectory(Guid runId) =>
        Path.Combine(LogsDirectoryPath, FormatRunId(runId));

    public string GetRunLogFilePath(Guid runId) =>
        Path.Combine(GetRunDirectory(runId), "run.log");

    public string GetSummaryFilePath(Guid runId) =>
        Path.Combine(GetRunDirectory(runId), "summary.json");

    public string GetSummaryTemporaryFilePath(Guid runId) =>
        Path.Combine(GetRunDirectory(runId), "summary.json.tmp");

    private static string FormatRunId(Guid runId)
    {
        if (runId == Guid.Empty)
        {
            throw new ArgumentException("A non-empty run identifier is required.", nameof(runId));
        }

        return runId.ToString("D");
    }
}
