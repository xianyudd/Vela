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

    public string CompactGateFilePath => Path.Combine(RootDirectory, "compact.lock");

    public static AppPaths CreateDefault() =>
        new(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Vela"));

    public string GetEventsFilePath(Guid runId) =>
        Path.Combine(GetRunDirectory(runId), "events.ndjson");

    public string GetPendingRequestFilePath(Guid runId) =>
        Path.Combine(PendingDirectoryPath, $"{FormatRunId(runId)}.json");

    public string GetPendingRequestTemporaryFilePath(Guid runId) =>
        Path.Combine(PendingDirectoryPath, $"{FormatRunId(runId)}.json.tmp");

    public string GetPendingRequestInflightFilePath(Guid runId) =>
        Path.Combine(PendingDirectoryPath, $"{FormatRunId(runId)}.json.inflight");

    public string GetRunDirectory(Guid runId) =>
        Path.Combine(LogsDirectoryPath, FormatRunId(runId));

    public string GetRunLogFilePath(Guid runId) =>
        Path.Combine(GetRunDirectory(runId), "run.log");

    public string GetJournalLockFilePath(Guid runId) =>
        Path.Combine(GetRunDirectory(runId), "journal.lock");

    public string GetSummaryFilePath(Guid runId) =>
        Path.Combine(GetRunDirectory(runId), "summary.json");

    public string GetSummaryTemporaryFilePath(Guid runId) =>
        Path.Combine(GetRunDirectory(runId), "summary.json.tmp");

    public bool IsExpectedPendingRequestPath(Guid runId, string? candidatePath)
    {
        if (runId == Guid.Empty)
        {
            return false;
        }

        return IsExactPath(GetPendingRequestFilePath(runId), candidatePath);
    }

    public bool IsExpectedPendingRequestInflightPath(Guid runId, string? candidatePath)
    {
        if (runId == Guid.Empty)
        {
            return false;
        }

        return IsExactPath(GetPendingRequestInflightFilePath(runId), candidatePath);
    }

    public bool IsExpectedRunDirectory(Guid runId, string? candidatePath)
    {
        if (runId == Guid.Empty)
        {
            return false;
        }

        return IsExactPath(GetRunDirectory(runId), candidatePath);
    }

    public bool IsTrustedRootDirectory() => IsTrustedPath(RootDirectory);

    public bool IsTrustedPendingDirectory() => IsTrustedPath(PendingDirectoryPath);

    public bool IsTrustedLogsDirectory() => IsTrustedPath(LogsDirectoryPath);

    public bool IsTrustedRunDirectory(Guid runId) =>
        runId != Guid.Empty &&
        IsTrustedPath(GetRunDirectory(runId));

    public bool IsTrustedPath(string? candidatePath)
    {
        if (string.IsNullOrWhiteSpace(candidatePath))
        {
            return false;
        }

        try
        {
            var fullPath = Path.GetFullPath(candidatePath);
            if (!IsWithinRoot(fullPath))
            {
                return false;
            }

            var existingAncestor = FindExistingAncestor(fullPath);
            if (existingAncestor is null)
            {
                return true;
            }

            var current = RootDirectory;
            if (PathExists(current) && HasReparsePoint(current))
            {
                return false;
            }

            var relative = Path.GetRelativePath(RootDirectory, existingAncestor);
            if (relative is "." or "")
            {
                return true;
            }

            foreach (var segment in relative.Split(
                         [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                         StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, segment);
                if (PathExists(current) && HasReparsePoint(current))
                {
                    return false;
                }
            }

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private bool IsWithinRoot(string fullPath)
    {
        var root = RootDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase) ||
               fullPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
               fullPath.StartsWith(root + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static string? FindExistingAncestor(string path)
    {
        var current = path;
        while (!PathExists(current))
        {
            var parent = Directory.GetParent(current)?.FullName;
            if (parent is null || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            current = parent;
        }

        return current;
    }

    private static bool PathExists(string path) =>
        File.Exists(path) || Directory.Exists(path) || TryGetAttributes(path, out _);

    private static bool HasReparsePoint(string path) =>
        TryGetAttributes(path, out var attributes) &&
        (attributes & FileAttributes.ReparsePoint) != 0;

    private static bool TryGetAttributes(string path, out FileAttributes attributes)
    {
        try
        {
            attributes = File.GetAttributes(path);
            return true;
        }
        catch (Exception)
        {
            attributes = default;
            return false;
        }
    }

    private static bool IsExactPath(string expectedPath, string? candidatePath)
    {
        if (string.IsNullOrWhiteSpace(candidatePath))
        {
            return false;
        }

        try
        {
            return string.Equals(
                Path.GetFullPath(expectedPath),
                Path.GetFullPath(candidatePath),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string FormatRunId(Guid runId)
    {
        if (runId == Guid.Empty)
        {
            throw new ArgumentException("A non-empty run identifier is required.", nameof(runId));
        }

        return runId.ToString("D");
    }
}
