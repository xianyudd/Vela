using System.Collections.Immutable;
using System.ComponentModel;
using System.Diagnostics;
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
    public string GetExecutablePath() =>
        Environment.ProcessPath
        ?? throw new InvalidOperationException("The current executable path is not available.");
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

            var startInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                UseShellExecute = true,
                Verb = "runas"
            };
            startInfo.ArgumentList.Add("--worker");
            startInfo.ArgumentList.Add("--run-id");
            startInfo.ArgumentList.Add(runId.ToString("D"));

            _processStarter.Start(startInfo);
            return Task.FromResult(
                new ElevatedWorkerLaunchResult(ElevatedWorkerLaunchStatus.Started));
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == UacCancelledErrorCode)
        {
            return Task.FromResult(
                new ElevatedWorkerLaunchResult(ElevatedWorkerLaunchStatus.Cancelled));
        }
        catch (Exception)
        {
            return Task.FromResult(
                new ElevatedWorkerLaunchResult(ElevatedWorkerLaunchStatus.Failed));
        }
    }
}

public enum ElevatedOperationStartStatus
{
    Started,
    ValidationFailed,
    Cancelled,
    Failed
}

public sealed record ElevatedOperationStartResult(
    ElevatedOperationStartStatus Status,
    TerminalResult? TerminalResult,
    string? RunDirectory);

public sealed class ElevatedOperationCoordinator
{
    private readonly IRunJournal _journal;
    private readonly IOperationRequestStore _requestStore;
    private readonly IElevatedWorkerLauncher _launcher;
    private readonly IClock _clock;

    public ElevatedOperationCoordinator(
        IRunJournal journal,
        IOperationRequestStore requestStore,
        IElevatedWorkerLauncher launcher,
        IClock clock)
    {
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(requestStore);
        ArgumentNullException.ThrowIfNull(launcher);
        ArgumentNullException.ThrowIfNull(clock);

        _journal = journal;
        _requestStore = requestStore;
        _launcher = launcher;
        _clock = clock;
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

        var created = await _journal.CreateRunAsync(request.RunId, cancellationToken).ConfigureAwait(false);
        if (!created.Succeeded || string.IsNullOrWhiteSpace(created.RunDirectory))
        {
            return new ElevatedOperationStartResult(
                ElevatedOperationStartStatus.ValidationFailed,
                TerminalResult.ValidationFailed,
                RunDirectory: null);
        }

        var written = await _requestStore.WriteAsync(request, cancellationToken).ConfigureAwait(false);
        if (!written.Succeeded)
        {
            return await CompleteParentFailureAsync(
                    request,
                    created.RunDirectory,
                    TerminalResult.ValidationFailed,
                    "PendingRequestWriteFailed",
                    consumeRequest: false,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var launched = await _launcher.LaunchAsync(request.RunId, cancellationToken).ConfigureAwait(false);
        return launched.Status switch
        {
            ElevatedWorkerLaunchStatus.Started => new ElevatedOperationStartResult(
                ElevatedOperationStartStatus.Started,
                TerminalResult: null,
                created.RunDirectory),
            ElevatedWorkerLaunchStatus.Cancelled => await CompleteParentFailureAsync(
                    request,
                    created.RunDirectory,
                    TerminalResult.CancelledBeforeElevation,
                    "UacCancelled",
                    consumeRequest: true,
                    cancellationToken)
                .ConfigureAwait(false),
            _ => await CompleteParentFailureAsync(
                    request,
                    created.RunDirectory,
                    TerminalResult.WorkerInterrupted,
                    "UacLaunchFailed",
                    consumeRequest: true,
                    cancellationToken)
                .ConfigureAwait(false)
        };
    }

    private async Task<ElevatedOperationStartResult> CompleteParentFailureAsync(
        OperationRequest request,
        string runDirectory,
        TerminalResult terminalResult,
        string operationName,
        bool consumeRequest,
        CancellationToken cancellationToken)
    {
        var occurredAtUtc = _clock.UtcNow;
        var appended = await _journal.AppendAsync(
                new RunEventDraft(
                    occurredAtUtc,
                    request.RunId,
                    RunPhase.Elevation,
                    RunEventLevel.Error,
                    operationName,
                    ImmutableArray<string>.Empty,
                    ExitCode: null,
                    Duration: null,
                    Output: null),
                cancellationToken)
            .ConfigureAwait(false);
        var summaryWritten = await _journal.WriteSummaryAsync(
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

        if (consumeRequest && appended.Succeeded && summaryWritten.Succeeded)
        {
            await _requestStore.ConsumeAsync(request.RunId, cancellationToken).ConfigureAwait(false);
        }

        return new ElevatedOperationStartResult(
            terminalResult == TerminalResult.CancelledBeforeElevation
                ? ElevatedOperationStartStatus.Cancelled
                : terminalResult == TerminalResult.ValidationFailed
                    ? ElevatedOperationStartStatus.ValidationFailed
                    : ElevatedOperationStartStatus.Failed,
            terminalResult,
            runDirectory);
    }

    private static bool IsValidCompactRequest(OperationRequest? request) =>
        request is not null &&
        request.RunId != Guid.Empty &&
        request.Intent == OperationIntent.Compact &&
        request.Profile is not null &&
        ProfileValidator.Validate(request.Profile).IsValid;
}
