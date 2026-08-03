using System.Collections.Immutable;
using System.Security.Principal;
using Vela.Core.Contracts;
using Vela.Core.Models;
using Vela.Core.Validation;
using Vela.Core.Workflows;
using Vela.Windows.Diagnostics;

namespace Vela.Tui.ProgramModes;

public sealed record WorkerArgumentParseResult(
    bool IsWorkerInvocation,
    bool IsValid,
    Guid? RunId);

public static class WorkerCommandLineParser
{
    public static WorkerArgumentParseResult Parse(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        if (arguments.Count == 0)
        {
            return new WorkerArgumentParseResult(false, false, null);
        }

        if (arguments.Count == 3 &&
            string.Equals(arguments[0], "--worker", StringComparison.Ordinal) &&
            string.Equals(arguments[1], "--run-id", StringComparison.Ordinal) &&
            Guid.TryParseExact(arguments[2], "D", out var runId) &&
            runId != Guid.Empty)
        {
            return new WorkerArgumentParseResult(true, true, runId);
        }

        return new WorkerArgumentParseResult(true, false, null);
    }
}

public static class WorkerExitCodes
{
    public static int FromTerminalResult(TerminalResult terminalResult) =>
        terminalResult switch
        {
            TerminalResult.Succeeded or TerminalResult.CompletedWithNoReclaim => 0,
            TerminalResult.ValidationFailed => 2,
            TerminalResult.ShutdownTimedOut => 3,
            TerminalResult.DiskPartPreflightFailed => 4,
            TerminalResult.DiskPartCompactFailed => 5,
            _ => 10
        };
}

public sealed record WorkerModeResult(
    TerminalResult TerminalResult,
    int ExitCode);

public interface IAdministratorProbe
{
    bool IsAdministrator();
}

public interface IWorkerOperationExecutor
{
    Task<WorkflowResult> ExecuteAsync(
        OperationRequest request,
        CancellationToken cancellationToken);
}

public sealed class WindowsAdministratorProbe : IAdministratorProbe
{
    public bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }
}

public sealed class PreflightWorkerOperationExecutor : IWorkerOperationExecutor
{
    private readonly PreflightWorkflow _preflightWorkflow;

    public PreflightWorkerOperationExecutor(PreflightWorkflow preflightWorkflow)
    {
        ArgumentNullException.ThrowIfNull(preflightWorkflow);
        _preflightWorkflow = preflightWorkflow;
    }

    public Task<WorkflowResult> ExecuteAsync(
        OperationRequest request,
        CancellationToken cancellationToken) =>
        _preflightWorkflow.ExecuteAsync(
            request,
            RunJournalAccessMode.OpenExisting,
            cancellationToken);
}

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
        Task.Delay(delay, cancellationToken);
}

public sealed class WorkerMode
{
    private static readonly Profile FallbackProfile = new(
        Guid.Empty,
        "Unknown",
        "Unknown",
        string.Empty,
        ShutdownMode.Global,
        TimeSpan.Zero);

    private readonly AppPaths _paths;
    private readonly IOperationRequestStore _requestStore;
    private readonly IRunJournal _journal;
    private readonly IAdministratorProbe _administratorProbe;
    private readonly ILxssProfileResolver _lxssProfileResolver;
    private readonly IWorkerOperationExecutor _executor;
    private readonly IClock _clock;

    public WorkerMode(
        AppPaths paths,
        IOperationRequestStore requestStore,
        IRunJournal journal,
        IAdministratorProbe administratorProbe,
        ILxssProfileResolver lxssProfileResolver,
        IWorkerOperationExecutor executor,
        IClock clock)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(requestStore);
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(administratorProbe);
        ArgumentNullException.ThrowIfNull(lxssProfileResolver);
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentNullException.ThrowIfNull(clock);

        _paths = paths;
        _requestStore = requestStore;
        _journal = journal;
        _administratorProbe = administratorProbe;
        _lxssProfileResolver = lxssProfileResolver;
        _executor = executor;
        _clock = clock;
    }

    public async Task<WorkerModeResult> RunAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var parsed = WorkerCommandLineParser.Parse(arguments);
        if (!parsed.IsValid || parsed.RunId is not Guid runId)
        {
            return CreateResult(TerminalResult.ValidationFailed);
        }

        if (!_paths.IsTrustedRootDirectory() ||
            !_paths.IsTrustedPendingDirectory() ||
            !_paths.IsTrustedLogsDirectory() ||
            !_paths.IsTrustedRunDirectory(runId))
        {
            return CreateResult(TerminalResult.ValidationFailed);
        }

        JournalOperationResult opened;
        try
        {
            opened = await _journal.OpenExistingRunAsync(runId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return CreateResult(TerminalResult.WorkerInterrupted);
        }

        if (opened is null ||
            !opened.Succeeded ||
            (opened.RunDirectory is null && _journal is FileRunJournal) ||
            (opened.RunDirectory is not null &&
             !_paths.IsExpectedRunDirectory(runId, opened.RunDirectory)))
        {
            return CreateResult(TerminalResult.ValidationFailed);
        }

        OperationRequestClaimResult claimed;
        try
        {
            claimed = await _requestStore.ClaimAsync(runId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return CreateResult(TerminalResult.WorkerInterrupted);
        }

        if (claimed is null || !claimed.Succeeded || claimed.Request is null)
        {
            return CreateResult(TerminalResult.ValidationFailed);
        }

        var request = claimed.Request;
        if (!IsValidStoredRequest(runId, request, claimed.SourcePath))
        {
            return await CompleteFailureAsync(
                    runId,
                    request,
                    "WorkerRequestInvalid",
                    RunPhase.Validation,
                    TerminalResult.ValidationFailed,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        bool isAdministrator;
        try
        {
            isAdministrator = _administratorProbe.IsAdministrator();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return await CompleteFailureAsync(
                    runId,
                    request,
                    "WorkerAdministratorProbeFailed",
                    RunPhase.Elevation,
                    TerminalResult.WorkerInterrupted,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (!isAdministrator)
        {
            return await CompleteFailureAsync(
                    runId,
                    request,
                    "WorkerNotElevated",
                    RunPhase.Elevation,
                    TerminalResult.ValidationFailed,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        LxssProfileResolution resolution;
        try
        {
            resolution = await _lxssProfileResolver
                .ResolveAsync(request.Profile.DistroName, request.Profile.VhdxPath, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return await CompleteFailureAsync(
                    runId,
                    request,
                    "WorkerLxssResolutionFailed",
                    RunPhase.Validation,
                    TerminalResult.ValidationFailed,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (resolution is null || !resolution.HasStrictMatchFor(request.Profile.DistroName))
        {
            return await CompleteFailureAsync(
                    runId,
                    request,
                    "WorkerLxssMappingMismatch",
                    RunPhase.Validation,
                    TerminalResult.ValidationFailed,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        try
        {
            var workflowResult = await _executor.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
            if (workflowResult is null || workflowResult.Summary is null)
            {
                return await CompleteFailureAsync(
                        runId,
                        request,
                        "WorkerWorkflowResultMissing",
                        RunPhase.Failed,
                        TerminalResult.WorkerInterrupted,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            if (workflowResult.Summary.RunId != runId)
            {
                return await CompleteFailureAsync(
                        runId,
                        request,
                        "WorkerSummaryRunIdMismatch",
                        RunPhase.Failed,
                        TerminalResult.WorkerInterrupted,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            return await CompleteWorkflowAsync(
                    runId,
                    request,
                    workflowResult,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return await CompleteFailureAsync(
                    runId,
                    request,
                    "WorkerUnhandledException",
                    RunPhase.Failed,
                    TerminalResult.WorkerInterrupted,
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task<WorkerModeResult> CompleteWorkflowAsync(
        Guid runId,
        OperationRequest request,
        WorkflowResult workflowResult,
        CancellationToken cancellationToken)
    {
        var summary = workflowResult.Summary;
        var terminalResult = summary.TerminalResult;
        var phase = terminalResult is TerminalResult.Succeeded or TerminalResult.CompletedWithNoReclaim
            ? RunPhase.Completed
            : RunPhase.Failed;
        var level = phase == RunPhase.Completed
            ? RunEventLevel.Information
            : RunEventLevel.Error;

        JournalAppendResult appended;
        try
        {
            appended = await _journal.AppendAsync(
                    new RunEventDraft(
                        _clock.UtcNow,
                        runId,
                        phase,
                        level,
                        phase == RunPhase.Completed ? "WorkerCompleted" : "WorkerFailed",
                        ImmutableArray<string>.Empty,
                        ExitCode: WorkerExitCodes.FromTerminalResult(terminalResult),
                        Duration: summary.CompletedAtUtc - summary.StartedAtUtc,
                        Output: null),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return CreateResult(TerminalResult.WorkerInterrupted);
        }

        if (!appended.Succeeded)
        {
            return CreateResult(TerminalResult.WorkerInterrupted);
        }

        JournalOperationResult summaryWritten;
        try
        {
            summaryWritten = await _journal
                .WriteSummaryAsync(summary with { RunId = runId }, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return CreateResult(TerminalResult.WorkerInterrupted);
        }

        if (!summaryWritten.Succeeded)
        {
            return CreateResult(TerminalResult.WorkerInterrupted);
        }

        return await ConsumeAndCreateResultAsync(runId, terminalResult, cancellationToken).ConfigureAwait(false);
    }

    private async Task<WorkerModeResult> CompleteFailureAsync(
        Guid runId,
        OperationRequest? request,
        string operationName,
        RunPhase phase,
        TerminalResult terminalResult,
        CancellationToken cancellationToken)
    {
        var occurredAtUtc = _clock.UtcNow;
        var profile = request?.Profile ?? FallbackProfile;
        var intent = request?.Intent ?? OperationIntent.Compact;

        JournalAppendResult appended;
        try
        {
            appended = await _journal.AppendAsync(
                    new RunEventDraft(
                        occurredAtUtc,
                        runId,
                        phase,
                        RunEventLevel.Error,
                        operationName,
                        ImmutableArray<string>.Empty,
                        ExitCode: WorkerExitCodes.FromTerminalResult(terminalResult),
                        Duration: null,
                        Output: null),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return CreateResult(TerminalResult.WorkerInterrupted);
        }

        if (!appended.Succeeded)
        {
            return CreateResult(TerminalResult.WorkerInterrupted);
        }

        JournalOperationResult summaryWritten;
        try
        {
            summaryWritten = await _journal.WriteSummaryAsync(
                    new RunSummary(
                        runId,
                        profile,
                        intent,
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
            return CreateResult(TerminalResult.WorkerInterrupted);
        }

        if (!summaryWritten.Succeeded)
        {
            return CreateResult(TerminalResult.WorkerInterrupted);
        }

        return await ConsumeAndCreateResultAsync(runId, terminalResult, cancellationToken).ConfigureAwait(false);
    }

    private async Task<WorkerModeResult> ConsumeAndCreateResultAsync(
        Guid runId,
        TerminalResult terminalResult,
        CancellationToken cancellationToken)
    {
        try
        {
            var consumed = await _requestStore
                .ConsumeAsync(runId, cancellationToken)
                .ConfigureAwait(false);
            return consumed.Succeeded
                ? CreateResult(terminalResult)
                : CreateResult(TerminalResult.WorkerInterrupted);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return CreateResult(TerminalResult.WorkerInterrupted);
        }
    }

    private bool IsValidStoredRequest(
        Guid runId,
        OperationRequest request,
        string? sourcePath) =>
        request.RunId == runId &&
        request.Intent == OperationIntent.Compact &&
        request.Profile is not null &&
        ProfileValidator.Validate(request.Profile).IsValid &&
        ( _paths.IsExpectedPendingRequestPath(runId, sourcePath) ||
          _paths.IsExpectedPendingRequestInflightPath(runId, sourcePath)) &&
        _paths.IsTrustedPath(sourcePath);

    private static WorkerModeResult CreateResult(TerminalResult terminalResult) =>
        new(terminalResult, WorkerExitCodes.FromTerminalResult(terminalResult));
}
