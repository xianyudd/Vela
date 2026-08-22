namespace Vela.Windows.Diagnostics;

/// <summary>
/// Counts what a retention sweep removed so the caller can report it.
/// </summary>
public sealed record RunLogRetentionResult(int RemovedRunDirectories)
{
    public static readonly RunLogRetentionResult None = new(0);

    public bool RemovedAnything => RemovedRunDirectories > 0;
}

/// <summary>
/// Prunes run directories older than the configured retention window. Every
/// launch writes a preflight run, so without this the logs directory grows
/// without bound and every history read pays for it.
/// </summary>
public sealed class RunLogRetention
{
    private readonly AppPaths _paths;

    public RunLogRetention(AppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _paths = paths;
    }

    /// <summary>
    /// Deletes run directories whose last write time is older than
    /// <paramref name="retentionDays"/>. Best effort: a directory that cannot be
    /// removed is skipped rather than failing the launch. Only trusted paths
    /// under the logs root that carry a well-formed run identifier are touched.
    /// </summary>
    public RunLogRetentionResult Prune(int retentionDays, Guid? protectedRunId = null)
    {
        if (retentionDays < 1 ||
            !_paths.IsTrustedLogsDirectory() ||
            !Directory.Exists(_paths.LogsDirectoryPath))
        {
            return RunLogRetentionResult.None;
        }

        var cutoffUtc = DateTime.UtcNow - TimeSpan.FromDays(retentionDays);
        string[] candidates;
        try
        {
            candidates = Directory.GetDirectories(_paths.LogsDirectoryPath);
        }
        catch (Exception)
        {
            return RunLogRetentionResult.None;
        }

        var removed = 0;
        foreach (var candidate in candidates)
        {
            if (TryPruneRunDirectory(candidate, cutoffUtc, protectedRunId))
            {
                removed++;
            }
        }

        return new RunLogRetentionResult(removed);
    }

    private bool TryPruneRunDirectory(string candidate, DateTime cutoffUtc, Guid? protectedRunId)
    {
        try
        {
            // Only ever delete a directory this application names itself: a
            // parseable run identifier whose canonical path matches exactly.
            if (!Guid.TryParseExact(Path.GetFileName(candidate), "D", out var runId) ||
                runId == protectedRunId ||
                !_paths.IsExpectedRunDirectory(runId, candidate) ||
                !_paths.IsTrustedRunDirectory(runId))
            {
                return false;
            }

            var info = new DirectoryInfo(candidate);
            if (!info.Exists ||
                (info.Attributes & FileAttributes.ReparsePoint) != 0 ||
                GetNewestWriteTimeUtc(info) > cutoffUtc)
            {
                return false;
            }

            info.Delete(recursive: true);
            return !Directory.Exists(candidate);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Uses the newest timestamp among the directory and its files so an
    /// in-progress run is never pruned because its directory stamp is stale.
    /// </summary>
    private static DateTime GetNewestWriteTimeUtc(DirectoryInfo directory)
    {
        var newest = directory.LastWriteTimeUtc;
        try
        {
            foreach (var file in directory.EnumerateFiles())
            {
                if (file.LastWriteTimeUtc > newest)
                {
                    newest = file.LastWriteTimeUtc;
                }
            }
        }
        catch (Exception)
        {
            // Fall back to the directory stamp alone.
        }

        return newest;
    }
}
