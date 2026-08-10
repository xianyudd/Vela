using System.Collections.Immutable;
using System.Text;
using System.Text.RegularExpressions;
using Vela.Core.Contracts;
using Vela.Core.Models;
using Vela.Windows.Processes;

namespace Vela.Windows.Storage;

public sealed class VhdxInspector : IVhdxInspector
{
    private static readonly Encoding WindowsChineseConsoleEncoding = CreateWindowsChineseConsoleEncoding();
    private static readonly Regex EnglishNegativeSparsePattern = new(
        @"\bnot\b.*\bsparse\b",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex ChineseNegativeSparsePattern = new(
        @"(?:没有|未|不是).*稀疏",
        RegexOptions.CultureInvariant);
    private readonly NativeToolPaths _nativeToolPaths;
    private readonly IProcessRunner _processRunner;

    public VhdxInspector()
        : this(new WindowsProcessRunner(), new NativeToolPaths())
    {
    }

    public VhdxInspector(IProcessRunner processRunner, NativeToolPaths nativeToolPaths)
    {
        ArgumentNullException.ThrowIfNull(processRunner);
        ArgumentNullException.ThrowIfNull(nativeToolPaths);
        _processRunner = processRunner;
        _nativeToolPaths = nativeToolPaths;
    }

    public async Task<VhdxInspectionResult> InspectAsync(
        string vhdxPath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        string? normalizedPath;
        try
        {
            normalizedPath = NormalizePath(vhdxPath);
        }
        catch (Exception)
        {
            normalizedPath = null;
        }

        if (normalizedPath is null)
        {
            return new VhdxInspectionResult(VhdxInspectionStatus.Failed, Snapshot: null);
        }

        try
        {
            var file = new FileInfo(normalizedPath);
            if (!file.Exists)
            {
                return new VhdxInspectionResult(VhdxInspectionStatus.Missing, Snapshot: null);
            }

            var driveRoot = Path.GetPathRoot(file.FullName);
            if (string.IsNullOrWhiteSpace(driveRoot))
            {
                return new VhdxInspectionResult(VhdxInspectionStatus.Failed, Snapshot: null);
            }

            var drive = new DriveInfo(driveRoot);
            var fileLengthBytes = file.Length;
            var lastWriteUtc = new DateTimeOffset(file.LastWriteTimeUtc);
            var sparse = await QuerySparseAsync(file.FullName, cancellationToken).ConfigureAwait(false);

            var snapshot = new VhdxSnapshot(
                DateTimeOffset.UtcNow,
                file.FullName,
                fileLengthBytes,
                lastWriteUtc,
                sparse,
                new DriveSnapshot(
                    drive.RootDirectory.FullName,
                    drive.TotalSize,
                    drive.AvailableFreeSpace));

            return new VhdxInspectionResult(VhdxInspectionStatus.Succeeded, snapshot);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return new VhdxInspectionResult(VhdxInspectionStatus.Failed, Snapshot: null);
        }
    }

    private async Task<bool?> QuerySparseAsync(string normalizedPath, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _processRunner.RunAsync(
                new ProcessInvocation(
                    _nativeToolPaths.FsutilExePath,
                    ImmutableArray.Create("sparse", "queryflag", normalizedPath),
                    Timeout: null,
                    OutputEncoding: WindowsChineseConsoleEncoding),
                output: null,
                cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            if (result.Status != ProcessExecutionStatus.Succeeded || result.ExitCode != 0)
            {
                return null;
            }

            return ParseSparseOutput(result.StandardOutput.Concat(result.StandardError));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) ||
            path.Any(char.IsControl) ||
            !Path.IsPathFullyQualified(path) ||
            !path.EndsWith(".vhdx", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return Path.GetFullPath(path);
    }

    private static bool? ParseSparseOutput(IEnumerable<string> output)
    {
        var text = string.Join(Environment.NewLine, output)
            .Replace("\0", string.Empty, StringComparison.Ordinal);
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        if (EnglishNegativeSparsePattern.IsMatch(text) ||
            ChineseNegativeSparsePattern.IsMatch(text))
        {
            return false;
        }

        if (text.Contains("sparse", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("稀疏", StringComparison.Ordinal))
        {
            return true;
        }

        return null;
    }

    private static Encoding CreateWindowsChineseConsoleEncoding()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(936);
    }
}
