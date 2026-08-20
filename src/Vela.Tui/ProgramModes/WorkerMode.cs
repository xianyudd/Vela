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
        TerminalResultSemantics.ToExitCode(terminalResult);
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

public sealed class CompactionWorkerOperationExecutor : IWorkerOperationExecutor
{
    private readonly CompactionWorkflow _compactionWorkflow;

    public CompactionWorkerOperationExecutor(CompactionWorkflow compactionWorkflow)
    {
        ArgumentNullException.ThrowIfNull(compactionWorkflow);
        _compactionWorkflow = compactionWorkflow;
    }

    public Task<WorkflowResult> ExecuteAsync(
        OperationRequest request,
        CancellationToken cancellationToken) =>
        _compactionWorkflow.ExecuteAsync(
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

        // Verify elevation before opening the journal or claiming/consuming the
        // pending request. A non-elevated worker must leave the operation
        // request and existing journal untouched so the rightful elevated
        // worker is not preempted.
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
            return CreateResult(TerminalResult.WorkerInterrupted);
        }

        if (!isAdministrator)
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
            return await CompleteUntrustedFailureAsync(
                    runId,
                    "WorkerRequestClaimFailed",
                    RunPhase.Validation,
                    TerminalResult.WorkerInterrupted,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (claimed is null || !claimed.Succeeded || claimed.Request is null)
        {
            return await CompleteUntrustedFailureAsync(
                    runId,
                    "WorkerRequestClaimFailed",
                    RunPhase.Validation,
                    TerminalResult.ValidationFailed,
                    cancellationToken)
                .ConfigureAwait(false);
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

            if (!TryValidateSummary(workflowResult.Summary, runId, request))
            {
                return await CompleteFailureAsync(
                        runId,
                        request,
                        "WorkerSummaryInvalid",
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
        var durableSummary = summary with
        {
            RunId = runId,
            TerminalResult = TerminalResultSemantics.NormalizeSummaryResult(summary)
        };
        var phase = durableSummary.TerminalResult is TerminalResult.Succeeded or TerminalResult.CompletedWithNoReclaim
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
                        ExitCode: WorkerExitCodes.FromTerminalResult(durableSummary.TerminalResult),
                        Duration: durableSummary.CompletedAtUtc - durableSummary.StartedAtUtc,
                        Output: null,
                        TerminalResult: durableSummary.TerminalResult),
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
                .WriteSummaryAsync(durableSummary, cancellationToken)
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
            // The terminal event is the authoritative lifecycle marker. A
            // summary is a history projection; its write failure must not
            // rewrite a completed operation as interrupted.
            return await ConsumeAndCreateResultAsync(
                    runId,
                    durableSummary.TerminalResult,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        return await ConsumeAndCreateResultAsync(
                runId,
                durableSummary.TerminalResult,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<WorkerModeResult> CompleteUntrustedFailureAsync(
        Guid runId,
        string operationName,
        RunPhase phase,
        TerminalResult terminalResult,
        CancellationToken cancellationToken)
    {
        try
        {
            await _journal.AppendAsync(
                    new RunEventDraft(
                        _clock.UtcNow,
                        runId,
                        phase,
                        RunEventLevel.Error,
                        operationName,
                        ImmutableArray<string>.Empty,
                        ExitCode: null,
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
        }

        try
        {
            var appended = await _journal.AppendAsync(
                    new RunEventDraft(
                        _clock.UtcNow,
                        runId,
                        RunPhase.Failed,
                        RunEventLevel.Error,
                        "WorkerFailed",
                        ImmutableArray<string>.Empty,
                        ExitCode: WorkerExitCodes.FromTerminalResult(terminalResult),
                        Duration: null,
                        Output: null,
                        TerminalResult: terminalResult),
                    cancellationToken)
                .ConfigureAwait(false);
            return appended.Succeeded
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

    private async Task<WorkerModeResult> CompleteFailureAsync(
        Guid runId,
        OperationRequest? request,
        string operationName,
        RunPhase phase,
        TerminalResult terminalResult,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return await CompleteUntrustedFailureAsync(
                    runId,
                    operationName,
                    phase,
                    terminalResult,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var occurredAtUtc = _clock.UtcNow;
        var profile = request.Profile;
        var intent = request.Intent;

        if (!string.Equals(operationName, "WorkerFailed", StringComparison.Ordinal))
        {
            try
            {
                await _journal.AppendAsync(
                        new RunEventDraft(
                            occurredAtUtc,
                            runId,
                            phase,
                            RunEventLevel.Error,
                            operationName,
                            ImmutableArray<string>.Empty,
                            ExitCode: null,
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
                // Preserve the canonical terminal publication even if diagnostic detail fails.
            }
        }

        JournalAppendResult appended;
        try
        {
            appended = await _journal.AppendAsync(
                    new RunEventDraft(
                        _clock.UtcNow,
                        runId,
                        RunPhase.Failed,
                        RunEventLevel.Error,
                        "WorkerFailed",
                        ImmutableArray<string>.Empty,
                        ExitCode: WorkerExitCodes.FromTerminalResult(terminalResult),
                        Duration: null,
                        Output: null,
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
            return CreateResult(TerminalResult.WorkerInterrupted);
        }

        if (!appended.Succeeded)
        {
            return CreateResult(TerminalResult.WorkerInterrupted);
        }

        var summary = new RunSummary(
            runId,
            profile,
            intent,
            occurredAtUtc,
            _clock.UtcNow,
            BeforeSnapshot: null,
            AfterSnapshot: null,
            terminalResult);

        JournalOperationResult summaryWritten;
        try
        {
            summaryWritten = await _journal.WriteSummaryAsync(summary, cancellationToken).ConfigureAwait(false);
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
            return await ConsumeAndCreateResultAsync(
                    runId,
                    terminalResult,
                    cancellationToken)
                .ConfigureAwait(false);
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
            if (consumed.Succeeded)
            {
                return CreateResult(terminalResult);
            }

            await AppendDiagnosticAsync(
                    runId,
                    "WorkerRequestConsumeFailed",
                    cancellationToken)
                .ConfigureAwait(false);
            return CreateResult(terminalResult);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            await AppendDiagnosticAsync(
                    runId,
                    "WorkerRequestConsumeFailed",
                    cancellationToken)
                .ConfigureAwait(false);
            return CreateResult(terminalResult);
        }
    }

    private async Task AppendDiagnosticAsync(
        Guid runId,
        string operationName,
        CancellationToken cancellationToken)
    {
        try
        {
            await _journal.AppendAsync(
                    new RunEventDraft(
                        _clock.UtcNow,
                        runId,
                        RunPhase.Failed,
                        RunEventLevel.Error,
                        operationName,
                        ImmutableArray<string>.Empty,
                        ExitCode: null,
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
        (_paths.IsExpectedPendingRequestPath(runId, sourcePath) ||
          _paths.IsExpectedPendingRequestInflightPath(runId, sourcePath)) &&
        _paths.IsTrustedPath(sourcePath);

    private static bool TryValidateSummary(
        RunSummary summary,
        Guid runId,
        OperationRequest request)
    {
        if (summary.RunId != runId ||
            summary.Profile != request.Profile ||
            summary.Intent != request.Intent ||
            !Enum.IsDefined(summary.TerminalResult) ||
            summary.StartedAtUtc > summary.CompletedAtUtc)
        {
            return false;
        }

        if (summary.TerminalResult is TerminalResult.Succeeded or TerminalResult.CompletedWithNoReclaim)
        {
            if (summary.BeforeSnapshot is null || summary.AfterSnapshot is null)
            {
                return false;
            }

            var expected = summary.ReclaimedBytes == 0
                ? TerminalResult.CompletedWithNoReclaim
                : TerminalResult.Succeeded;
            return summary.TerminalResult == expected;
        }

        return true;
    }

    private static WorkerModeResult CreateResult(TerminalResult terminalResult) =>
        new(terminalResult, WorkerExitCodes.FromTerminalResult(terminalResult));
}
