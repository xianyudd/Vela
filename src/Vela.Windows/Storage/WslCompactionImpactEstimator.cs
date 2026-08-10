using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Vela.Core.Contracts;
using Vela.Windows.Processes;

namespace Vela.Windows.Storage;

/// <summary>
/// Estimates reclaimable VHDX bytes from the selected distro's current guest
/// usage. The estimate is read-only: it runs df inside the target and never
/// invokes WSL shutdown, terminate, trim, or DiskPart.
/// </summary>
public sealed class WslCompactionImpactEstimator : ICompactionImpactEstimator
{
    private static readonly Regex UsageColumns = new(
        @"(?:^|\s)(?<total>\d+)\s+(?<used>\d+)\s+(?<available>\d+)\s+\d+%",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private readonly IProcessRunner _processRunner;
    private readonly NativeToolPaths _nativeToolPaths;

    public WslCompactionImpactEstimator()
        : this(new WindowsProcessRunner(), new NativeToolPaths())
    {
    }

    public WslCompactionImpactEstimator(
        IProcessRunner processRunner,
        NativeToolPaths nativeToolPaths)
    {
        ArgumentNullException.ThrowIfNull(processRunner);
        ArgumentNullException.ThrowIfNull(nativeToolPaths);
        _processRunner = processRunner;
        _nativeToolPaths = nativeToolPaths;
    }

    public async Task<CompactionImpactEstimate> EstimateAsync(
        string distroName,
        long currentVhdxSizeBytes,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(distroName) ||
            distroName.Any(char.IsControl) ||
            currentVhdxSizeBytes < 0)
        {
            return Unavailable(currentVhdxSizeBytes, "目标存储使用量暂不可用。");
        }

        ProcessExecutionResult result;
        try
        {
            result = await _processRunner.RunAsync(
                    new ProcessInvocation(
                        _nativeToolPaths.WslExePath,
                        ImmutableArray.Create(
                            "--distribution",
                            distroName,
                            "--",
                            "df",
                            "-B1",
                            "-P",
                            "/"),
                        Timeout: TimeSpan.FromSeconds(15),
                        OutputEncoding: Encoding.Unicode),
                    output: null,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return new CompactionImpactEstimate(
                CompactionImpactStatus.Failed,
                currentVhdxSizeBytes,
                null,
                null,
                "目标存储使用量采集失败。");
        }

        if (result.Status != ProcessExecutionStatus.Succeeded || result.ExitCode != 0)
        {
            return Unavailable(currentVhdxSizeBytes, "目标存储使用量暂不可用。");
        }

        if (!TryParseUsage(result.StandardOutput.Concat(result.StandardError), out var usage))
        {
            return Unavailable(currentVhdxSizeBytes, "目标存储使用量暂不可用。");
        }

        var reclaimableBytes = Math.Clamp(
            currentVhdxSizeBytes - usage.UsedBytes,
            0,
            currentVhdxSizeBytes);
        return new CompactionImpactEstimate(
            CompactionImpactStatus.Estimated,
            currentVhdxSizeBytes,
            usage.UsedBytes,
            reclaimableBytes,
            "按当前 VHDX 体积减去根文件系统已用空间估算。");
    }

    private static CompactionImpactEstimate Unavailable(long currentVhdxSizeBytes, string message) =>
        new(
            CompactionImpactStatus.Unavailable,
            currentVhdxSizeBytes >= 0 ? currentVhdxSizeBytes : null,
            null,
            null,
            message);

    private static bool TryParseUsage(
        IEnumerable<string> output,
        out GuestUsage usage)
    {
        foreach (var line in output)
        {
            var match = UsageColumns.Match(line);
            if (!match.Success ||
                !long.TryParse(
                    match.Groups["total"].Value,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var totalBytes) ||
                !long.TryParse(
                    match.Groups["used"].Value,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var usedBytes) ||
                !long.TryParse(
                    match.Groups["available"].Value,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var availableBytes) ||
                totalBytes < 0 ||
                usedBytes < 0 ||
                availableBytes < 0)
            {
                continue;
            }

            usage = new GuestUsage(totalBytes, usedBytes, availableBytes);
            return true;
        }

        usage = default;
        return false;
    }

    private readonly record struct GuestUsage(
        long TotalBytes,
        long UsedBytes,
        long AvailableBytes);
}
