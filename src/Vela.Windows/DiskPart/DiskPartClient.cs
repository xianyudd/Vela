using System.Collections.Immutable;
using System.Text;
using Vela.Core.Contracts;
using Vela.Core.Models;
using Vela.Windows.Processes;

namespace Vela.Windows.DiskPart;

public sealed class DiskPartClient : IDiskPartClient
{
    // diskpart is a legacy console tool: on a zh-CN Windows it emits its output
    // in the OEM console code page (cp936/GBK), not UTF-16. Mirror the proven
    // decoding VhdxInspector already uses for fsutil so localized success/error
    // phrases round-trip into recognizable .NET strings instead of mojibake.
    private static readonly Encoding WindowsConsoleEncoding = CreateWindowsConsoleEncoding();

    // Positive proof that `compact vdisk` actually reclaimed the file. The
    // `select vdisk` line that precedes it ALSO contains "successfully"/"成功",
    // so a compact success marker must name the compaction, never a bare success
    // word. If a future locale phrases this differently the run.log tail written
    // by LogClassificationAsync surfaces the real wording so this list can grow.
    private static readonly string[] CompactSuccessMarkers =
    {
        "successfully compacted", // en: "DiskPart successfully compacted the virtual disk file."
        "成功压缩",                // zh: "DiskPart 已成功压缩虚拟磁盘文件。"
    };

    // Any of these means diskpart failed regardless of the process exit code.
    // The ERROR_SHARING_VIOLATION that motivated this guard (the WSL utility VM
    // still holds ext4.vhdx) surfaces here as a "used by another process" line
    // while diskpart may still exit 0.
    private static readonly string[] DiskPartErrorMarkers =
    {
        "encountered an error",          // en: "DiskPart has encountered an error:"
        "access is denied",              // en
        "being used by another process", // en
        "cannot access the file",        // en
        "遇到错误",                       // zh: DiskPart 遇到错误
        "拒绝访问",                       // zh: access denied
        "另一个进程正在使用",              // zh: used by another process
        "正在被另一个进程使用",            // zh variant
        "无法访问",                       // zh: cannot access the file
    };

    // Only the tail of diskpart's output is worth keeping for troubleshooting —
    // the decisive success/error line comes last. The trusted journal does not
    // bound Output on its own, so bound it here before it is written.
    private const int MaxLoggedOutputChars = 512;

    private readonly IProcessRunner _processRunner;
    private readonly NativeToolPaths _nativeToolPaths;
    private readonly DiskPartScriptBuilder _scriptBuilder;
    private readonly IPrivilegedDiskPartWorkspace _workspace;
    private readonly IRunJournal? _journal;

    public DiskPartClient()
        : this(
            new WindowsProcessRunner(),
            new NativeToolPaths(),
            new DiskPartScriptBuilder(),
            new PrivilegedDiskPartWorkspace())
    {
    }

    public DiskPartClient(IRunJournal? journal)
        : this(
            new WindowsProcessRunner(),
            new NativeToolPaths(),
            new DiskPartScriptBuilder(),
            new PrivilegedDiskPartWorkspace(),
            journal)
    {
    }

    public DiskPartClient(
        IProcessRunner processRunner,
        NativeToolPaths nativeToolPaths,
        DiskPartScriptBuilder scriptBuilder,
        IPrivilegedDiskPartWorkspace workspace,
        IRunJournal? journal = null)
    {
        ArgumentNullException.ThrowIfNull(processRunner);
        ArgumentNullException.ThrowIfNull(nativeToolPaths);
        ArgumentNullException.ThrowIfNull(scriptBuilder);
        ArgumentNullException.ThrowIfNull(workspace);

        _processRunner = processRunner;
        _nativeToolPaths = nativeToolPaths;
        _scriptBuilder = scriptBuilder;
        _workspace = workspace;
        _journal = journal;
    }

    private enum DiskPartOperation
    {
        Detail,
        Compact
    }

    public Task<ProcessExecutionResult> DetailVdiskAsync(
        Guid runId,
        string validatedVhdxPath,
        CancellationToken cancellationToken) =>
        RunAsync(
            runId,
            validatedVhdxPath,
            _scriptBuilder.BuildDetailScript,
            DiskPartOperation.Detail,
            cancellationToken);

    public Task<ProcessExecutionResult> CompactVdiskAsync(
        Guid runId,
        string validatedVhdxPath,
        CancellationToken cancellationToken) =>
        RunAsync(
            runId,
            validatedVhdxPath,
            _scriptBuilder.BuildCompactScript,
            DiskPartOperation.Compact,
            cancellationToken);

    private async Task<ProcessExecutionResult> RunAsync(
        Guid runId,
        string validatedVhdxPath,
        Func<string, string> scriptFactory,
        DiskPartOperation operation,
        CancellationToken cancellationToken)
    {
        if (runId == Guid.Empty)
        {
            throw new ArgumentException("The run identifier must not be empty.", nameof(runId));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var script = scriptFactory(validatedVhdxPath);

        await using var lease = await _workspace
            .CreateScriptAsync(runId, script, cancellationToken)
            .ConfigureAwait(false);

        await lease.VerifyAsync(cancellationToken).ConfigureAwait(false);

        var result = await _processRunner
            .RunAsync(
                new ProcessInvocation(
                    _nativeToolPaths.DiskPartExePath,
                    ImmutableArray.Create("/s", lease.ScriptPath),
                    Timeout: null,
                    OutputEncoding: WindowsConsoleEncoding),
                output: null,
                cancellationToken)
            .ConfigureAwait(false);

        await lease.VerifyAsync(cancellationToken).ConfigureAwait(false);

        // Exit code alone is unreliable: diskpart can exit 0 while `compact vdisk`
        // logically failed. Reinterpret the result from its own output so the
        // workflow's IsSuccessful check (Status == Succeeded) fails closed.
        var classified = Classify(result, operation, out var decision);
        await LogClassificationAsync(runId, operation, result, classified, decision, cancellationToken)
            .ConfigureAwait(false);
        return classified;
    }

    private static ProcessExecutionResult Classify(
        ProcessExecutionResult result,
        DiskPartOperation operation,
        out string decision)
    {
        // A runner that already reported a non-success status (timeout, cancel,
        // launch failure, non-zero exit) is authoritative — never upgrade it.
        if (result.Status != ProcessExecutionStatus.Succeeded)
        {
            decision = $"runner status={result.Status}; left unchanged";
            return result;
        }

        var text = BuildScanText(result);
        var hasError = ContainsAny(text, DiskPartErrorMarkers);

        if (operation == DiskPartOperation.Compact)
        {
            var hasSuccess = ContainsAny(text, CompactSuccessMarkers);
            if (hasError || !hasSuccess)
            {
                // Strict fail-closed: without a positive compaction marker (or with
                // any error marker) we refuse to report success.
                decision = $"compact: error-marker={hasError} success-marker={hasSuccess} -> Failed (strict fail-closed)";
                return result with { Status = ProcessExecutionStatus.Failed };
            }

            decision = "compact: success-marker=yes error-marker=no -> Succeeded";
            return result;
        }

        // Detail has no positive marker and only gates preflight, so it is lenient:
        // downgrade only when diskpart explicitly reports an error.
        if (hasError)
        {
            decision = "detail: error-marker=yes -> Failed";
            return result with { Status = ProcessExecutionStatus.Failed };
        }

        decision = "detail: error-marker=no -> Succeeded";
        return result;
    }

    private async Task LogClassificationAsync(
        Guid runId,
        DiskPartOperation operation,
        ProcessExecutionResult rawResult,
        ProcessExecutionResult classifiedResult,
        string decision,
        CancellationToken cancellationToken)
    {
        if (_journal is null)
        {
            return;
        }

        // Error when the final verdict is a failure (the actionable case), Trace
        // otherwise. This event is an additive, bounded, classified companion to
        // the workflow's own diskpart event — it never repeats the full output.
        var isFailure = classifiedResult.Status != ProcessExecutionStatus.Succeeded;
        var draft = new RunEventDraft(
            DateTimeOffset.UtcNow,
            runId,
            operation == DiskPartOperation.Compact ? RunPhase.Compacting : RunPhase.DiskPartPreflight,
            isFailure ? RunEventLevel.Error : RunEventLevel.Trace,
            operation == DiskPartOperation.Compact ? "DiskPartCompactClassified" : "DiskPartDetailClassified",
            ImmutableArray.Create(decision),
            rawResult.ExitCode,
            rawResult.Duration,
            BuildBoundedOutput(rawResult, decision),
            TerminalResult: null);

        try
        {
            await _journal.AppendAsync(draft, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // Diagnostics are best effort: the classified result is authoritative
            // and the workflow records its own canonical diskpart event regardless.
        }
    }

    private static string BuildBoundedOutput(ProcessExecutionResult result, string decision)
    {
        var text = BuildScanText(result);
        if (text.Length > MaxLoggedOutputChars)
        {
            // Keep the tail: diskpart's decisive success/error line comes last.
            text = "…" + text[^MaxLoggedOutputChars..];
        }

        return $"decision={decision}; exit={result.ExitCode?.ToString() ?? "null"}; tail={text}";
    }

    private static string BuildScanText(ProcessExecutionResult result) =>
        string.Join(
                "\n",
                result.StandardOutput.Concat(result.StandardError).Where(static line => line is not null))
            .Replace("\0", string.Empty, StringComparison.Ordinal);

    private static bool ContainsAny(string text, string[] markers) =>
        markers.Any(marker => text.Contains(marker, StringComparison.OrdinalIgnoreCase));

    private static Encoding CreateWindowsConsoleEncoding()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(936);
    }
}
