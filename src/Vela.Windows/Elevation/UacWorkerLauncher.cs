using System.Collections.Immutable;
using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using Vela.Core.Contracts;
using Vela.Core.Models;
using Vela.Core.Validation;

namespace Vela.Windows.Elevation;

public interface IExecutablePathProvider
{
    string GetExecutablePath();
}

public interface IUacProcessStarter
{
    void Start(ProcessStartInfo startInfo);
}

public sealed class CurrentExecutablePathProvider : IExecutablePathProvider
{
    public string GetExecutablePath()
    {
        var entryAssemblyPath = Assembly.GetEntryAssembly()?.Location;
        if (!string.IsNullOrWhiteSpace(entryAssemblyPath))
        {
            var directory = Path.GetDirectoryName(entryAssemblyPath);
            var appHostPath = Path.Combine(
                directory ?? string.Empty,
                Path.GetFileNameWithoutExtension(entryAssemblyPath) + ".exe");
            if (File.Exists(appHostPath))
            {
                return appHostPath;
            }
        }

        return Environment.ProcessPath
            ?? throw new InvalidOperationException("The current executable path is not available.");
    }
}

public sealed class ProcessUacProcessStarter : IUacProcessStarter
{
    public void Start(ProcessStartInfo startInfo)
    {
        ArgumentNullException.ThrowIfNull(startInfo);

        if (Process.Start(startInfo) is null)
        {
            throw new InvalidOperationException("The elevated worker process did not start.");
        }
    }
}

public sealed class UacWorkerLauncher : IElevatedWorkerLauncher
{
    private const int UacCancelledErrorCode = 1223;
    private const int ElevationRequiredErrorCode = 740;
    private readonly IExecutablePathProvider _executablePathProvider;
    private readonly IUacProcessStarter _processStarter;

    public UacWorkerLauncher()
        : this(new CurrentExecutablePathProvider(), new ProcessUacProcessStarter())
    {
    }

    public UacWorkerLauncher(
        IExecutablePathProvider executablePathProvider,
        IUacProcessStarter processStarter)
    {
        ArgumentNullException.ThrowIfNull(executablePathProvider);
        ArgumentNullException.ThrowIfNull(processStarter);

        _executablePathProvider = executablePathProvider;
        _processStarter = processStarter;
    }

    public Task<ElevatedWorkerLaunchResult> LaunchAsync(
        Guid runId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (runId == Guid.Empty)
        {
            return Task.FromResult(
                new ElevatedWorkerLaunchResult(ElevatedWorkerLaunchStatus.Rejected));
        }

        try
        {
            var executablePath = _executablePathProvider.GetExecutablePath();
            if (string.IsNullOrWhiteSpace(executablePath) || !Path.IsPathFullyQualified(executablePath))
            {
                return Task.FromResult(
                    new ElevatedWorkerLaunchResult(ElevatedWorkerLaunchStatus.Rejected));
            }

            // The interactive process already runs elevated (app.manifest
            // declares requireAdministrator), so a plain CreateProcess is enough:
            // the child inherits the parent's administrator token directly and
            // Windows raises no prompt for it either way. What the shell route
            // does cost is a visible console window - UseShellExecute ignores
            // CreateNoWindow - which would flash over the interface on every
            // compaction. The worker talks only through the run journal and needs
            // no console, so both flags below are load-bearing: UseShellExecute
            // must be false for CreateNoWindow to take effect at all.
            var startInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            var entryAssemblyPath = Assembly.GetEntryAssembly()?.Location;
            if (IsDotnetHost(executablePath) &&
                !string.IsNullOrWhiteSpace(entryAssemblyPath))
            {
                startInfo.ArgumentList.Add(entryAssemblyPath);
            }
            startInfo.ArgumentList.Add("--worker");
            startInfo.ArgumentList.Add("--run-id");
            startInfo.ArgumentList.Add(runId.ToString("D"));

            _processStarter.Start(startInfo);
            return Task.FromResult(
                new ElevatedWorkerLaunchResult(ElevatedWorkerLaunchStatus.Started));
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == ElevationRequiredErrorCode)
        {
            // The worker demands elevation the launching process does not hold.
            // With the manifest in force this cannot happen; it is the signature
            // of the manifest being bypassed - "dotnet run" against the project
            // ignores it - and the generic Failed status leaves nothing in the
            // journal to explain a launch that fails every single time.
            return Task.FromResult(
                new ElevatedWorkerLaunchResult(
                    ElevatedWorkerLaunchStatus.Rejected,
                    "The worker requires an elevated token and this process does not hold one. " +
                    "Start the built executable, which requests elevation through its manifest."));
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == UacCancelledErrorCode)
        {
            // Unreachable while the launch above is a plain CreateProcess, which
            // shows no prompt to cancel. Retained deliberately: it is the only
            // mapping onto CancelledBeforeElevation, so reintroducing any
            // consent-raising launch keeps reporting a declined prompt as a
            // cancellation rather than as a generic failure.
            return Task.FromResult(
                new ElevatedWorkerLaunchResult(ElevatedWorkerLaunchStatus.Cancelled));
        }
        catch (Exception)
        {
            return Task.FromResult(
                new ElevatedWorkerLaunchResult(ElevatedWorkerLaunchStatus.Failed));
        }
    }

    private static bool IsDotnetHost(string executablePath) =>
        string.Equals(
            Path.GetFileNameWithoutExtension(executablePath),
            "dotnet",
            StringComparison.OrdinalIgnoreCase);
}

public enum ElevatedOperationStartStatus
{
    Started,
    ValidationFailed,
    AlreadyRunning,
    Cancelled,
    Failed
}

public sealed record ElevatedOperationStartResult(
    ElevatedOperationStartStatus Status,
    TerminalResult? TerminalResult,
    string? RunDirectory,
    Guid? ActiveRunId = null,
    string? ActiveGatePath = null,
    CompactRunGateLease? GateLease = null);

public sealed class ElevatedOperationCoordinator
{
    private readonly IRunJournal _journal;
    private readonly IOperationRequestStore _requestStore;
    private readonly IElevatedWorkerLauncher _launcher;
    private readonly IClock _clock;
    private readonly CompactRunGate? _runGate;

    public ElevatedOperationCoordinator(
        IRunJournal journal,
        IOperationRequestStore requestStore,
        IElevatedWorkerLauncher launcher,
        IClock clock,
        CompactRunGate? runGate = null)
    {
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(requestStore);
        ArgumentNullException.ThrowIfNull(launcher);
        ArgumentNullException.ThrowIfNull(clock);

        _journal = journal;
        _requestStore = requestStore;
        _launcher = launcher;
        _clock = clock;
        _runGate = runGate;
    }

    public async Task<ElevatedOperationStartResult> StartAsync(
        OperationRequest request,
        CancellationToken cancellationToken)
    {
        if (!IsValidCompactRequest(request))
        {
            return new ElevatedOperationStartResult(
                ElevatedOperationStartStatus.ValidationFailed,
                TerminalResult.ValidationFailed,
                RunDirectory: null);
        }

        var gate = _runGate?.TryAcquire(request);
        if (gate is { Status: CompactRunGateStatus.AlreadyRunning })
        {
            return new ElevatedOperationStartResult(
                ElevatedOperationStartStatus.AlreadyRunning,
                TerminalResult.WorkerInterrupted,
                gate.RunDirectory,
                gate.ActiveRunId,
                gate.GatePath);
        }

        if (gate is { Status: CompactRunGateStatus.Invalid })
        {
            return new ElevatedOperationStartResult(
                ElevatedOperationStartStatus.ValidationFailed,
                TerminalResult.ValidationFailed,
                RunDirectory: null,
                ActiveGatePath: gate.GatePath);
        }

        var gateLease = gate?.Lease;
        string? runDirectory = null;
        var gateLeaseTransferred = false;

        try
        {
            var created = await _journal.CreateRunAsync(request.RunId, cancellationToken).ConfigureAwait(false);
            runDirectory = created.RunDirectory;
            if (!created.Succeeded || string.IsNullOrWhiteSpace(runDirectory))
            {
                _runGate?.Release(request.RunId);
                return new ElevatedOperationStartResult(
                    ElevatedOperationStartStatus.ValidationFailed,
                    TerminalResult.ValidationFailed,
                    runDirectory);
            }

            var written = await _requestStore.WriteAsync(request, cancellationToken).ConfigureAwait(false);
            if (!written.Succeeded)
            {
                return await CompleteParentFailureAsync(
                        request,
                        runDirectory,
                    TerminalResult.ValidationFailed,
                    "PendingRequestWriteFailed",
                    consumeRequest: false,
                    cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
            }

            var launched = await _launcher.LaunchAsync(request.RunId, cancellationToken).ConfigureAwait(false);
            if (launched.Status == ElevatedWorkerLaunchStatus.Started)
            {
                gateLeaseTransferred = true;
                return new ElevatedOperationStartResult(
                    ElevatedOperationStartStatus.Started,
                    TerminalResult: null,
                    runDirectory,
                    GateLease: gateLease);
            }

            return await CompleteParentFailureAsync(
                    request,
                    runDirectory,
                    launched.Status == ElevatedWorkerLaunchStatus.Cancelled
                        ? TerminalResult.CancelledBeforeElevation
                        : TerminalResult.WorkerInterrupted,
                    launched.Status == ElevatedWorkerLaunchStatus.Cancelled
                        ? "UacCancelled"
                        : "UacLaunchFailed",
                    consumeRequest: true,
                    failureReason: launched.FailureReason,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return new ElevatedOperationStartResult(
                ElevatedOperationStartStatus.Failed,
                TerminalResult.WorkerInterrupted,
                runDirectory);
        }
        finally
        {
            if (!gateLeaseTransferred)
            {
                gateLease?.Dispose();
            }
        }
    }

    private async Task<ElevatedOperationStartResult> CompleteParentFailureAsync(
        OperationRequest request,
        string runDirectory,
        TerminalResult terminalResult,
        string operationName,
        bool consumeRequest,
        CancellationToken cancellationToken,
        string? failureReason = null)
    {
        var occurredAtUtc = _clock.UtcNow;
        JournalAppendResult appended;
        try
        {
            appended = await _journal.AppendAsync(
                    new RunEventDraft(
                        _clock.UtcNow,
                        request.RunId,
                        RunPhase.Elevation,
                        RunEventLevel.Error,
                        operationName,
                        ImmutableArray<string>.Empty,
                        ExitCode: TerminalResultSemantics.ToExitCode(terminalResult),
                        Duration: null,
                        Output: failureReason,
                        TerminalResult: terminalResult),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return new ElevatedOperationStartResult(
                ElevatedOperationStartStatus.Failed,
                terminalResult,
                runDirectory);
        }

        if (!appended.Succeeded)
        {
            return new ElevatedOperationStartResult(
                ElevatedOperationStartStatus.Failed,
                terminalResult,
                runDirectory);
        }

        JournalOperationResult summaryWritten;
        try
        {
            summaryWritten = await _journal.WriteSummaryAsync(
                    new RunSummary(
                        request.RunId,
                        request.Profile,
                        request.Intent,
                        occurredAtUtc,
                        _clock.UtcNow,
                        BeforeSnapshot: null,
                        AfterSnapshot: null,
                        terminalResult),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            summaryWritten = JournalOperationResult.Failure();
        }

        if (!summaryWritten.Succeeded)
        {
            await TryConsumeRequestAsync(request.RunId, consumeRequest, cancellationToken).ConfigureAwait(false);
            return CreateParentFailureResult(terminalResult, runDirectory);
        }

        if (consumeRequest)
        {
            try
            {
                var consumed = await _requestStore.ConsumeAsync(request.RunId, cancellationToken).ConfigureAwait(false);
                if (!consumed.Succeeded)
                {
                    _runGate?.Release(request.RunId);
                    return new ElevatedOperationStartResult(
                        ElevatedOperationStartStatus.Failed,
                        terminalResult,
                        runDirectory);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                _runGate?.Release(request.RunId);
                return new ElevatedOperationStartResult(
                    ElevatedOperationStartStatus.Failed,
                    terminalResult,
                    runDirectory);
            }
        }

        _runGate?.Release(request.RunId);

        return new ElevatedOperationStartResult(
            terminalResult == TerminalResult.CancelledBeforeElevation
                ? ElevatedOperationStartStatus.Cancelled
                : terminalResult == TerminalResult.ValidationFailed
                    ? ElevatedOperationStartStatus.ValidationFailed
                    : ElevatedOperationStartStatus.Failed,
            terminalResult,
            runDirectory);

    }

    private async Task TryConsumeRequestAsync(
        Guid runId,
        bool consumeRequest,
        CancellationToken cancellationToken)
    {
        if (!consumeRequest)
        {
            return;
        }

        try
        {
            _ = await _requestStore.ConsumeAsync(runId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // The canonical terminal event remains authoritative; cleanup is
            // best effort after summary persistence has already failed.
        }
    }

    private static ElevatedOperationStartResult CreateParentFailureResult(
        TerminalResult terminalResult,
        string runDirectory) =>
        new(
            terminalResult == TerminalResult.CancelledBeforeElevation
                ? ElevatedOperationStartStatus.Cancelled
                : terminalResult == TerminalResult.ValidationFailed
                    ? ElevatedOperationStartStatus.ValidationFailed
                    : ElevatedOperationStartStatus.Failed,
            terminalResult,
            runDirectory);

    private static bool IsValidCompactRequest(OperationRequest? request) =>
        request is not null &&
        request.RunId != Guid.Empty &&
        request.Intent == OperationIntent.Compact &&
        request.Profile is not null &&
        ProfileValidator.Validate(request.Profile).IsValid;
}
