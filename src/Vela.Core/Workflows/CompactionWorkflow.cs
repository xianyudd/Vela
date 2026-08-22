using System.Collections.Immutable;
using Vela.Core.Contracts;
using Vela.Core.Models;
using Vela.Core.Validation;

namespace Vela.Core.Workflows;

public sealed class CompactionWorkflow
{
    private readonly IWslClient _wslClient;
    private readonly ILxssProfileResolver _lxssProfileResolver;
    private readonly IVhdxInspector _vhdxInspector;
    private readonly IDiskPartClient _diskPartClient;
    private readonly IRunJournal _runJournal;
    private readonly IClock _clock;
    private readonly TimeSpan _pollInterval;

    public CompactionWorkflow(
        IWslClient wslClient,
        ILxssProfileResolver lxssProfileResolver,
        IVhdxInspector vhdxInspector,
        IDiskPartClient diskPartClient,
        IRunJournal runJournal,
        IClock clock,
        TimeSpan? pollInterval = null)
    {
        ArgumentNullException.ThrowIfNull(wslClient);
        ArgumentNullException.ThrowIfNull(lxssProfileResolver);
        ArgumentNullException.ThrowIfNull(vhdxInspector);
        ArgumentNullException.ThrowIfNull(diskPartClient);
        ArgumentNullException.ThrowIfNull(runJournal);
        ArgumentNullException.ThrowIfNull(clock);

        _wslClient = wslClient;
        _lxssProfileResolver = lxssProfileResolver;
        _vhdxInspector = vhdxInspector;
        _diskPartClient = diskPartClient;
        _runJournal = runJournal;
        _clock = clock;
        _pollInterval = pollInterval is { } configured && configured > TimeSpan.Zero
            ? configured
            : TimeSpan.FromMilliseconds(250);
    }

    public Task<WorkflowResult> ExecuteAsync(
        OperationRequest request,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(request, RunJournalAccessMode.Create, cancellationToken);

    public async Task<WorkflowResult> ExecuteAsync(
        OperationRequest request,
        RunJournalAccessMode journalAccessMode,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Profile);
        cancellationToken.ThrowIfCancellationRequested();

        var startedAtUtc = _clock.UtcNow;
        var validation = ProfileValidator.Validate(request.Profile);
        var diagnostics = CreateRequestDiagnostics(request, validation, journalAccessMode);
        var report = new PreflightReport(validation, null, null, null, null);
        var canUseJournal = request.RunId != Guid.Empty && Enum.IsDefined(journalAccessMode);
        string? runDirectory = null;
        var journalOpened = false;

        if (canUseJournal)
        {
            var opened = await TryOpenJournalAsync(
                    request.RunId,
                    journalAccessMode,
                    cancellationToken)
                .ConfigureAwait(false);
            journalOpened = opened is not null && opened.Succeeded;
            runDirectory = opened?.RunDirectory;
            if (!journalOpened)
            {
                diagnostics = AddJournalFailureDiagnostic(diagnostics);
            }
        }

        if (!journalOpened && canUseJournal)
        {
            return await CompleteAsync(
                    request,
                    startedAtUtc,
                    report,
                    diagnostics,
                    TerminalResult.ValidationFailed,
                    journalOpened,
                    journalAccessMode,
                    runDirectory,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (HasErrors(diagnostics))
        {
            return await CompleteAsync(
                    request,
                    startedAtUtc,
                    report,
                    diagnostics,
                    TerminalResult.ValidationFailed,
                    journalOpened,
                    journalAccessMode,
                    runDirectory,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        diagnostics = await AppendOrDiagnoseAsync(
                diagnostics,
                new RunEventDraft(
                    _clock.UtcNow,
                    request.RunId,
                    RunPhase.Validation,
                    RunEventLevel.Information,
                    "Profile validation",
                    ImmutableArray<string>.Empty,
                    null,
                    null,
                    "Profile validation succeeded."),
                journalOpened,
                cancellationToken)
            .ConfigureAwait(false);

        WslInventory? installedInventory;
        try
        {
            installedInventory = await _wslClient
                .GetInstalledInventoryAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            installedInventory = null;
        }

        if (installedInventory is null)
        {
            diagnostics = AddDiagnostic(
                diagnostics,
                WorkflowDiagnosticCode.InstalledInventoryFailed,
                RunPhase.Inventory,
                RunEventLevel.Error,
                "The installed WSL distribution inventory could not be read.");
            return await CompleteAsync(
                    request,
                    startedAtUtc,
                    report,
                    diagnostics,
                    TerminalResult.ValidationFailed,
                    journalOpened,
                    journalAccessMode,
                    runDirectory,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        report = report with { InstalledInventory = installedInventory };
        diagnostics = await AppendOrDiagnoseAsync(
                diagnostics,
                new RunEventDraft(
                    installedInventory.CapturedAtUtc,
                    request.RunId,
                    RunPhase.Inventory,
                    RunEventLevel.Information,
                    "WSL installed inventory",
                    installedInventory.Distributions
                        .Select(static distribution => distribution.Name)
                        .ToImmutableArray(),
                    null,
                    null,
                    "Installed WSL distributions were collected."),
                journalOpened,
                cancellationToken)
            .ConfigureAwait(false);

        if (!ContainsDistribution(installedInventory, request.Profile.DistroName))
        {
            diagnostics = AddDiagnostic(
                diagnostics,
                WorkflowDiagnosticCode.DistroNotInstalled,
                RunPhase.Inventory,
                RunEventLevel.Error,
                "The selected WSL distribution is not installed.");
            return await CompleteAsync(
                    request,
                    startedAtUtc,
                    report,
                    diagnostics,
                    TerminalResult.ValidationFailed,
                    journalOpened,
                    journalAccessMode,
                    runDirectory,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        LxssProfileResolution? resolution;
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
            resolution = null;
        }

        resolution ??= new LxssProfileResolution(
            LxssResolutionStatus.Failed,
            request.Profile.DistroName,
            null,
            null);

        report = report with { LxssResolution = resolution };
        var mappingMatches = resolution.HasStrictMatchFor(request.Profile.DistroName);
        diagnostics = await AppendOrDiagnoseAsync(
                diagnostics,
                new RunEventDraft(
                    _clock.UtcNow,
                    request.RunId,
                    RunPhase.Validation,
                    mappingMatches ? RunEventLevel.Information : RunEventLevel.Error,
                    "Lxss profile mapping",
                    CreateMappingArguments(resolution),
                    null,
                    null,
                    "The Lxss profile mapping was evaluated."),
                journalOpened,
                cancellationToken)
            .ConfigureAwait(false);

        if (!mappingMatches)
        {
            diagnostics = AddDiagnostic(
                diagnostics,
                resolution.Status switch
                {
                    LxssResolutionStatus.NotFound => WorkflowDiagnosticCode.LxssResolutionNotFound,
                    LxssResolutionStatus.Failed => WorkflowDiagnosticCode.LxssResolutionFailed,
                    _ => WorkflowDiagnosticCode.LxssMappingMismatch
                },
                RunPhase.Validation,
                RunEventLevel.Error,
                "The Lxss mapping does not match the requested VHDX path.");
            return await CompleteAsync(
                    request,
                    startedAtUtc,
                    report,
                    diagnostics,
                    TerminalResult.ValidationFailed,
                    journalOpened,
                    journalAccessMode,
                    runDirectory,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var beforeInspection = await InspectAsync(request.Profile.VhdxPath, cancellationToken).ConfigureAwait(false);
        report = report with { VhdxInspection = beforeInspection };
        diagnostics = AddInspectionDiagnostics(diagnostics, beforeInspection);
        diagnostics = await AppendOrDiagnoseAsync(
                diagnostics,
                CreateInspectionEvent(request.RunId, beforeInspection, "VHDX before snapshot"),
                journalOpened,
                cancellationToken)
            .ConfigureAwait(false);

        if (beforeInspection.Status != VhdxInspectionStatus.Succeeded || beforeInspection.Snapshot is null)
        {
            return await CompleteAsync(
                    request,
                    startedAtUtc,
                    report,
                    diagnostics,
                    TerminalResult.ValidationFailed,
                    journalOpened,
                    journalAccessMode,
                    runDirectory,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        WslInventory? runningInventory;
        try
        {
            runningInventory = await _wslClient
                .GetRunningInventoryAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            runningInventory = null;
        }

        if (runningInventory is null)
        {
            diagnostics = AddDiagnostic(
                diagnostics,
                WorkflowDiagnosticCode.RunningInventoryFailed,
                RunPhase.Inventory,
                RunEventLevel.Error,
                "The running WSL distribution inventory could not be read.");
            return await CompleteAsync(
                    request,
                    startedAtUtc,
                    report,
                    diagnostics,
                    TerminalResult.ValidationFailed,
                    journalOpened,
                    journalAccessMode,
                    runDirectory,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        report = report with { RunningInventory = runningInventory };
        diagnostics = await AppendOrDiagnoseAsync(
                diagnostics,
                new RunEventDraft(
                    runningInventory.CapturedAtUtc,
                    request.RunId,
                    RunPhase.Inventory,
                    RunEventLevel.Information,
                    "WSL running inventory",
                    runningInventory.Distributions
                        .Select(static distribution => distribution.Name)
                        .ToImmutableArray(),
                    null,
                    null,
                    "Running WSL distributions were collected."),
                journalOpened,
                cancellationToken)
            .ConfigureAwait(false);

        ProcessExecutionResult shutdownResult;
        try
        {
            shutdownResult = request.Profile.ShutdownMode == ShutdownMode.Global
                ? await _wslClient.ShutdownAllAsync(cancellationToken).ConfigureAwait(false)
                : await _wslClient.TerminateDistroAsync(request.Profile.DistroName, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            shutdownResult = FailedProcessResult(exception);
        }

        diagnostics = await AppendOrDiagnoseAsync(
                diagnostics,
                new RunEventDraft(
                    _clock.UtcNow,
                    request.RunId,
                    RunPhase.Shutdown,
                    IsSuccessful(shutdownResult) ? RunEventLevel.Information : RunEventLevel.Error,
                    request.Profile.ShutdownMode == ShutdownMode.Global ? "WSL --shutdown" : "WSL --terminate",
                    request.Profile.ShutdownMode == ShutdownMode.Global
                        ? ImmutableArray.Create("--shutdown")
                        : ImmutableArray.Create("--terminate", request.Profile.DistroName),
                    shutdownResult?.ExitCode,
                    shutdownResult?.Duration,
                    CreateProcessOutput(shutdownResult)),
                journalOpened,
                cancellationToken)
            .ConfigureAwait(false);

        if (!IsSuccessful(shutdownResult))
        {
            diagnostics = AddDiagnostic(
                diagnostics,
                WorkflowDiagnosticCode.ShutdownTimedOut,
                RunPhase.Shutdown,
                RunEventLevel.Error,
                "The WSL shutdown action did not complete successfully.");
            return await CompleteAsync(
                    request,
                    startedAtUtc,
                    report,
                    diagnostics,
                    TerminalResult.ShutdownTimedOut,
                    journalOpened,
                    journalAccessMode,
                    runDirectory,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var wait = await WaitForShutdownAsync(
                request.Profile,
                cancellationToken)
            .ConfigureAwait(false);
        if (!wait.ReachedTarget)
        {
            diagnostics = AddDiagnostic(
                diagnostics,
                wait.ReadFailed ? WorkflowDiagnosticCode.RunningInventoryFailed : WorkflowDiagnosticCode.ShutdownTimedOut,
                RunPhase.Shutdown,
                RunEventLevel.Error,
                wait.ReadFailed
                    ? "The running WSL distribution inventory could not be read while waiting for shutdown."
                    : "The WSL shutdown timeout elapsed before the target state was reached.");
            diagnostics = await AppendOrDiagnoseAsync(
                    diagnostics,
                    new RunEventDraft(
                        _clock.UtcNow,
                        request.RunId,
                        RunPhase.Shutdown,
                        RunEventLevel.Error,
                        "WSL shutdown timeout",
                        wait.RunningNames,
                        null,
                        null,
                        "The target WSL shutdown state was not reached."),
                    journalOpened,
                    cancellationToken)
                .ConfigureAwait(false);
            return await CompleteAsync(
                    request,
                    startedAtUtc,
                    report with { RunningInventory = wait.LastInventory },
                    diagnostics,
                    TerminalResult.ShutdownTimedOut,
                    journalOpened,
                    journalAccessMode,
                    runDirectory,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        report = report with { RunningInventory = wait.LastInventory };

        ProcessExecutionResult detailResult;
        try
        {
            detailResult = await _diskPartClient
                .DetailVdiskAsync(request.RunId, resolution.ResolvedVhdxPath!, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            detailResult = FailedProcessResult(exception);
        }

        diagnostics = await AppendOrDiagnoseAsync(
                diagnostics,
                CreateDiskPartEvent(request.RunId, "DiskPart detail vdisk", RunPhase.DiskPartPreflight, detailResult),
                journalOpened,
                cancellationToken)
            .ConfigureAwait(false);

        if (!IsSuccessful(detailResult))
        {
            diagnostics = AddDiagnostic(
                diagnostics,
                WorkflowDiagnosticCode.DiskPartPreflightFailed,
                RunPhase.DiskPartPreflight,
                RunEventLevel.Error,
                "DiskPart detail vdisk failed.");
            return await CompleteAsync(
                    request,
                    startedAtUtc,
                    report,
                    diagnostics,
                    TerminalResult.DiskPartPreflightFailed,
                    journalOpened,
                    journalAccessMode,
                    runDirectory,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        ProcessExecutionResult compactResult;
        try
        {
            compactResult = await _diskPartClient
                .CompactVdiskAsync(request.RunId, resolution.ResolvedVhdxPath!, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            compactResult = FailedProcessResult(exception);
        }

        diagnostics = await AppendOrDiagnoseAsync(
                diagnostics,
                CreateDiskPartEvent(request.RunId, "DiskPart compact vdisk", RunPhase.Compacting, compactResult),
                journalOpened,
                cancellationToken)
            .ConfigureAwait(false);

        if (!IsSuccessful(compactResult))
        {
            diagnostics = AddDiagnostic(
                diagnostics,
                WorkflowDiagnosticCode.DiskPartCompactFailed,
                RunPhase.Compacting,
                RunEventLevel.Error,
                "DiskPart compact vdisk failed.");
            return await CompleteAsync(
                    request,
                    startedAtUtc,
                    report,
                    diagnostics,
                    TerminalResult.DiskPartCompactFailed,
                    journalOpened,
                    journalAccessMode,
                    runDirectory,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var afterInspection = await InspectAsync(request.Profile.VhdxPath, cancellationToken).ConfigureAwait(false);
        diagnostics = AddInspectionDiagnostics(diagnostics, afterInspection);
        diagnostics = await AppendOrDiagnoseAsync(
                diagnostics,
                CreateInspectionEvent(request.RunId, afterInspection, "VHDX after snapshot"),
                journalOpened,
                cancellationToken)
            .ConfigureAwait(false);

        if (afterInspection.Status != VhdxInspectionStatus.Succeeded || afterInspection.Snapshot is null)
        {
            diagnostics = AddDiagnostic(
                diagnostics,
                WorkflowDiagnosticCode.VhdxInspectionFailed,
                RunPhase.Snapshot,
                RunEventLevel.Error,
                "The VHDX after snapshot could not be collected.");
            return await CompleteAsync(
                    request,
                    startedAtUtc,
                    report,
                    diagnostics,
                    TerminalResult.DiskPartCompactFailed,
                    journalOpened,
                    journalAccessMode,
                    runDirectory,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var finalReport = report with { VhdxInspection = beforeInspection };
        var reclaimedBytes = Math.Max(
            0,
            beforeInspection.Snapshot.FileLengthBytes - afterInspection.Snapshot.FileLengthBytes);
        var terminalResult = reclaimedBytes == 0
            ? TerminalResult.CompletedWithNoReclaim
            : TerminalResult.Succeeded;
        diagnostics = await AppendOrDiagnoseAsync(
                diagnostics,
                new RunEventDraft(
                    _clock.UtcNow,
                    request.RunId,
                    RunPhase.Completed,
                    RunEventLevel.Information,
                    "Compaction completed",
                    ImmutableArray.Create(reclaimedBytes.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                    null,
                    null,
                    reclaimedBytes == 0
                        ? "Compaction completed with no reclaimed bytes."
                        : "Compaction completed."),
                journalOpened,
                cancellationToken)
            .ConfigureAwait(false);

        return await CompleteAsync(
                request,
                startedAtUtc,
                finalReport,
                diagnostics,
                terminalResult,
                journalOpened,
                journalAccessMode,
                runDirectory,
                cancellationToken,
                afterInspection.Snapshot)
            .ConfigureAwait(false);
    }

    private async Task<WorkflowResult> CompleteAsync(
        OperationRequest request,
        DateTimeOffset startedAtUtc,
        PreflightReport report,
        ImmutableArray<WorkflowDiagnostic> diagnostics,
        TerminalResult terminalResult,
        bool journalOpened,
        RunJournalAccessMode journalAccessMode,
        string? runDirectory,
        CancellationToken cancellationToken,
        VhdxSnapshot? afterSnapshot = null)
    {
        var summary = new RunSummary(
            request.RunId,
            request.Profile,
            request.Intent,
            startedAtUtc,
            _clock.UtcNow,
            report.VhdxInspection?.Snapshot,
            afterSnapshot,
            terminalResult);

        if (journalOpened && journalAccessMode == RunJournalAccessMode.Create)
        {
            try
            {
                var written = await _runJournal
                    .WriteSummaryAsync(summary, cancellationToken)
                    .ConfigureAwait(false);
                if (written is null || !written.Succeeded)
                {
                    diagnostics = AddJournalFailureDiagnostic(diagnostics);
                }
                else if (runDirectory is null)
                {
                    runDirectory = written.RunDirectory;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                diagnostics = AddJournalFailureDiagnostic(diagnostics);
            }
        }

        return new WorkflowResult(summary, report, diagnostics, runDirectory);
    }

    private async Task<JournalOperationResult?> TryOpenJournalAsync(
        Guid runId,
        RunJournalAccessMode accessMode,
        CancellationToken cancellationToken)
    {
        try
        {
            return accessMode == RunJournalAccessMode.Create
                ? await _runJournal.CreateRunAsync(runId, cancellationToken).ConfigureAwait(false)
                : await _runJournal.OpenExistingRunAsync(runId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return JournalOperationResult.Failure();
        }
    }

    private async Task<ImmutableArray<WorkflowDiagnostic>> AppendOrDiagnoseAsync(
        ImmutableArray<WorkflowDiagnostic> diagnostics,
        RunEventDraft eventDraft,
        bool journalOpened,
        CancellationToken cancellationToken)
    {
        if (!journalOpened)
        {
            return diagnostics;
        }

        try
        {
            var appended = await _runJournal.AppendAsync(eventDraft, cancellationToken).ConfigureAwait(false);
            return appended is null || !appended.Succeeded
                ? AddJournalFailureDiagnostic(diagnostics)
                : diagnostics;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return AddJournalFailureDiagnostic(diagnostics);
        }
    }

    private async Task<VhdxInspectionResult> InspectAsync(
        string path,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _vhdxInspector.InspectAsync(path, cancellationToken).ConfigureAwait(false)
                ?? new VhdxInspectionResult(VhdxInspectionStatus.Failed, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return new VhdxInspectionResult(VhdxInspectionStatus.Failed, null);
        }
    }

    private async Task<WaitResult> WaitForShutdownAsync(
        Profile profile,
        CancellationToken cancellationToken)
    {
        var elapsed = TimeSpan.Zero;
        WslInventory? lastInventory = null;

        while (true)
        {
            try
            {
                lastInventory = await _wslClient
                    .GetRunningInventoryAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (lastInventory is null)
                {
                    return new WaitResult(false, true, null, ImmutableArray<string>.Empty);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                return new WaitResult(false, true, lastInventory, ImmutableArray<string>.Empty);
            }

            if (ReachedTarget(profile, lastInventory))
            {
                return new WaitResult(true, false, lastInventory, RunningNames(lastInventory));
            }

            if (elapsed >= profile.ShutdownTimeout)
            {
                return new WaitResult(false, false, lastInventory, RunningNames(lastInventory));
            }

            var delay = _pollInterval;
            if (elapsed + delay > profile.ShutdownTimeout)
            {
                delay = profile.ShutdownTimeout - elapsed;
            }

            await _clock.DelayAsync(delay, cancellationToken).ConfigureAwait(false);
            elapsed += delay;
        }
    }

    private static bool ReachedTarget(Profile profile, WslInventory inventory) =>
        profile.ShutdownMode == ShutdownMode.Global
            ? !inventory.Distributions.Any(static distribution => distribution.State == WslDistributionState.Running)
            : !inventory.Distributions.Any(distribution =>
                string.Equals(distribution.Name, profile.DistroName, StringComparison.Ordinal) &&
                distribution.State == WslDistributionState.Running);

    private static ImmutableArray<string> RunningNames(WslInventory? inventory) =>
        inventory?.Distributions
            .Where(static distribution => distribution.State == WslDistributionState.Running)
            .Select(static distribution => distribution.Name)
            .ToImmutableArray() ?? ImmutableArray<string>.Empty;

    private static RunEventDraft CreateInspectionEvent(
        Guid runId,
        VhdxInspectionResult inspection,
        string operationName) =>
        new(
            inspection.Snapshot?.CapturedAtUtc ?? DateTimeOffset.UtcNow,
            runId,
            RunPhase.Snapshot,
            inspection.Status == VhdxInspectionStatus.Succeeded
                ? RunEventLevel.Information
                : RunEventLevel.Error,
            operationName,
            ImmutableArray<string>.Empty,
            null,
            null,
            "The VHDX snapshot was evaluated.");

    private static RunEventDraft CreateDiskPartEvent(
        Guid runId,
        string operationName,
        RunPhase phase,
        ProcessExecutionResult? result) =>
        new(
            result?.CompletedAtUtc ?? DateTimeOffset.UtcNow,
            runId,
            phase,
            IsSuccessful(result) ? RunEventLevel.Information : RunEventLevel.Error,
            operationName,
            ImmutableArray<string>.Empty,
            result?.ExitCode,
            result?.Duration,
            CreateProcessOutput(result));

    private static ImmutableArray<WorkflowDiagnostic> CreateRequestDiagnostics(
        OperationRequest request,
        ValidationResult validation,
        RunJournalAccessMode journalAccessMode)
    {
        var diagnostics = ImmutableArray<WorkflowDiagnostic>.Empty;
        if (request.RunId == Guid.Empty)
        {
            diagnostics = AddDiagnostic(diagnostics, WorkflowDiagnosticCode.RequestInvalid, RunPhase.Validation, RunEventLevel.Error, "The run identifier must not be empty.");
        }

        if (request.Intent != OperationIntent.Compact)
        {
            diagnostics = AddDiagnostic(diagnostics, WorkflowDiagnosticCode.RequestInvalid, RunPhase.Validation, RunEventLevel.Error, "The compaction workflow only accepts Compact requests.");
        }

        if (!Enum.IsDefined(journalAccessMode))
        {
            diagnostics = AddDiagnostic(diagnostics, WorkflowDiagnosticCode.RequestInvalid, RunPhase.Validation, RunEventLevel.Error, "The run journal access mode is not supported.");
        }

        foreach (var error in validation.Errors)
        {
            diagnostics = AddDiagnostic(diagnostics, WorkflowDiagnosticCode.ProfileValidationFailed, RunPhase.Validation, RunEventLevel.Error, error.Message);
        }

        return diagnostics;
    }

    private static ImmutableArray<WorkflowDiagnostic> AddInspectionDiagnostics(
        ImmutableArray<WorkflowDiagnostic> diagnostics,
        VhdxInspectionResult inspection) =>
        inspection.Status switch
        {
            VhdxInspectionStatus.Succeeded when inspection.Snapshot is not null => diagnostics,
            VhdxInspectionStatus.Missing => AddDiagnostic(diagnostics, WorkflowDiagnosticCode.VhdxMissing, RunPhase.Snapshot, RunEventLevel.Error, "The requested VHDX file was not found."),
            _ => AddDiagnostic(diagnostics, WorkflowDiagnosticCode.VhdxInspectionFailed, RunPhase.Snapshot, RunEventLevel.Error, "The VHDX could not be inspected.")
        };

    private static ImmutableArray<string> CreateMappingArguments(LxssProfileResolution resolution)
    {
        var arguments = ImmutableArray<string>.Empty;
        if (!string.IsNullOrWhiteSpace(resolution.NormalizedRequestedVhdxPath))
        {
            arguments = arguments.Add(resolution.NormalizedRequestedVhdxPath);
        }

        if (!string.IsNullOrWhiteSpace(resolution.ResolvedVhdxPath))
        {
            arguments = arguments.Add(resolution.ResolvedVhdxPath);
        }

        return arguments;
    }

    private static bool ContainsDistribution(WslInventory inventory, string distroName) =>
        inventory.Distributions.Any(distribution =>
            string.Equals(distribution.Name, distroName, StringComparison.Ordinal));

    private static bool HasErrors(ImmutableArray<WorkflowDiagnostic> diagnostics) =>
        diagnostics.Any(static diagnostic => diagnostic.Level == RunEventLevel.Error);

    private static ImmutableArray<WorkflowDiagnostic> AddJournalFailureDiagnostic(
        ImmutableArray<WorkflowDiagnostic> diagnostics) =>
        diagnostics.Any(static diagnostic => diagnostic.Code == WorkflowDiagnosticCode.JournalFailure)
            ? diagnostics
            : AddDiagnostic(diagnostics, WorkflowDiagnosticCode.JournalFailure, RunPhase.Failed, RunEventLevel.Error, "Run diagnostics could not be persisted.");

    private static ImmutableArray<WorkflowDiagnostic> AddDiagnostic(
        ImmutableArray<WorkflowDiagnostic> diagnostics,
        WorkflowDiagnosticCode code,
        RunPhase phase,
        RunEventLevel level,
        string message) =>
        diagnostics.Add(new WorkflowDiagnostic(code, phase, level, message));

    private static bool IsSuccessful(ProcessExecutionResult? result) =>
        result is not null &&
        result.Status == ProcessExecutionStatus.Succeeded &&
        result.ExitCode == 0;

    private static ProcessExecutionResult FailedProcessResult(Exception exception) => new(
        ProcessExecutionStatus.Failed,
        null,
        ImmutableArray<string>.Empty,
        DescribeException(exception),
        DateTimeOffset.UtcNow,
        DateTimeOffset.UtcNow);

    /// <summary>
    /// Renders an exception that prevented a process from launching into the
    /// standard-error lines of the synthetic failure result, so the run journal
    /// records why the step failed instead of an empty output.
    /// </summary>
    /// <remarks>
    /// Type name and message only — never the stack trace: this text reaches the
    /// trusted journal, and the journal is also the source the display projection
    /// sanitises. The chain is walked so wrapped Win32 failures (the actual cause
    /// is usually the innermost one) survive, but depth and length are bounded so
    /// a pathological chain cannot flood the journal.
    /// </remarks>
    private static ImmutableArray<string> DescribeException(Exception exception)
    {
        const int maxDepth = 4;
        const int maxMessageLength = 512;

        var lines = ImmutableArray.CreateBuilder<string>();
        var current = exception;
        for (var depth = 0; current is not null && depth < maxDepth; depth++)
        {
            var message = current.Message ?? string.Empty;
            if (message.Length > maxMessageLength)
            {
                message = string.Concat(message.AsSpan(0, maxMessageLength), "…");
            }

            lines.Add($"{current.GetType().FullName}: {message}");
            current = current.InnerException;
        }

        return lines.ToImmutable();
    }

    private static string? CreateProcessOutput(ProcessExecutionResult? result)
    {
        if (result is null)
        {
            return null;
        }

        var output = result.StandardOutput
            .Concat(result.StandardError)
            .Where(static line => line is not null)
            .ToArray();
        return output.Length == 0 ? null : string.Join(Environment.NewLine, output);
    }

    private sealed record WaitResult(
        bool ReachedTarget,
        bool ReadFailed,
        WslInventory? LastInventory,
        ImmutableArray<string> RunningNames);
}
