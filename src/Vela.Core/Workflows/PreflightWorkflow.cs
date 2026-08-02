using System.Collections.Immutable;
using Vela.Core.Contracts;
using Vela.Core.Models;
using Vela.Core.Validation;

namespace Vela.Core.Workflows;

public sealed class PreflightWorkflow
{
    private readonly IWslInventoryReader _wslClient;
    private readonly ILxssProfileResolver _lxssProfileResolver;
    private readonly IVhdxInspector _vhdxInspector;
    private readonly IRunJournal _runJournal;
    private readonly IClock _clock;

    public PreflightWorkflow(
        IWslInventoryReader wslClient,
        ILxssProfileResolver lxssProfileResolver,
        IVhdxInspector vhdxInspector,
        IRunJournal runJournal,
        IClock clock)
    {
        ArgumentNullException.ThrowIfNull(wslClient);
        ArgumentNullException.ThrowIfNull(lxssProfileResolver);
        ArgumentNullException.ThrowIfNull(vhdxInspector);
        ArgumentNullException.ThrowIfNull(runJournal);
        ArgumentNullException.ThrowIfNull(clock);

        _wslClient = wslClient;
        _lxssProfileResolver = lxssProfileResolver;
        _vhdxInspector = vhdxInspector;
        _runJournal = runJournal;
        _clock = clock;
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

        if (HasErrors(diagnostics))
        {
            return await CompleteAsync(
                    request,
                    startedAtUtc,
                    report,
                    diagnostics,
                    journalAccessMode,
                    canUseJournal,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        WslInventory? installedInventory = null;

        try
        {
            installedInventory = await _wslClient
                .GetInstalledInventoryAsync(cancellationToken)
                .ConfigureAwait(false);

            if (installedInventory is null)
            {
                diagnostics = AddDiagnostic(
                    diagnostics,
                    WorkflowDiagnosticCode.InstalledInventoryFailed,
                    RunPhase.Inventory,
                    RunEventLevel.Error,
                    "The installed WSL distribution inventory was empty.");
            }
            else
            {
                report = report with { InstalledInventory = installedInventory };
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            diagnostics = AddDiagnostic(
                diagnostics,
                WorkflowDiagnosticCode.InstalledInventoryFailed,
                RunPhase.Inventory,
                RunEventLevel.Error,
                "The installed WSL distribution inventory could not be read.");
        }

        if (installedInventory is not null)
        {
            if (ContainsDistribution(installedInventory, request.Profile.DistroName))
            {
                var resolution = await ResolveAsync(request.Profile, diagnostics, cancellationToken)
                    .ConfigureAwait(false);
                diagnostics = resolution.Diagnostics;
                report = report with { LxssResolution = resolution.Value };
            }
            else
            {
                diagnostics = AddDiagnostic(
                    diagnostics,
                    WorkflowDiagnosticCode.DistroNotInstalled,
                    RunPhase.Inventory,
                    RunEventLevel.Error,
                    "The selected WSL distribution is not installed.");
            }

            var inspection = await InspectAsync(request.Profile.VhdxPath, diagnostics, cancellationToken)
                .ConfigureAwait(false);
            diagnostics = inspection.Diagnostics;
            report = report with { VhdxInspection = inspection.Value };
        }

        try
        {
            var runningInventory = await _wslClient
                .GetRunningInventoryAsync(cancellationToken)
                .ConfigureAwait(false);

            if (runningInventory is null)
            {
                diagnostics = AddDiagnostic(
                    diagnostics,
                    WorkflowDiagnosticCode.RunningInventoryFailed,
                    RunPhase.Inventory,
                    RunEventLevel.Error,
                    "The running WSL distribution inventory was empty.");
            }
            else
            {
                report = report with { RunningInventory = runningInventory };
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            diagnostics = AddDiagnostic(
                diagnostics,
                WorkflowDiagnosticCode.RunningInventoryFailed,
                RunPhase.Inventory,
                RunEventLevel.Error,
                "The running WSL distribution inventory could not be read.");
        }

        return await CompleteAsync(
                request,
                startedAtUtc,
                report,
                diagnostics,
                journalAccessMode,
                canUseJournal,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<CollectedValue<LxssProfileResolution>> ResolveAsync(
        Profile profile,
        ImmutableArray<WorkflowDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        try
        {
            var resolution = await _lxssProfileResolver
                .ResolveAsync(profile.DistroName, profile.VhdxPath, cancellationToken)
                .ConfigureAwait(false);
            var updatedDiagnostics = resolution.Status switch
            {
                LxssResolutionStatus.Matched when resolution.HasStrictMatchFor(profile.DistroName) => diagnostics,
                LxssResolutionStatus.Matched or LxssResolutionStatus.Mismatched => AddDiagnostic(
                    diagnostics,
                    WorkflowDiagnosticCode.LxssMappingMismatch,
                    RunPhase.Validation,
                    RunEventLevel.Error,
                    "The Lxss mapping does not match the requested VHDX path."),
                LxssResolutionStatus.NotFound => AddDiagnostic(
                    diagnostics,
                    WorkflowDiagnosticCode.LxssResolutionNotFound,
                    RunPhase.Validation,
                    RunEventLevel.Error,
                    "No Lxss mapping was found for the selected WSL distribution."),
                LxssResolutionStatus.Failed => AddDiagnostic(
                    diagnostics,
                    WorkflowDiagnosticCode.LxssResolutionFailed,
                    RunPhase.Validation,
                    RunEventLevel.Error,
                    "The Lxss mapping could not be resolved."),
                _ => AddDiagnostic(
                    diagnostics,
                    WorkflowDiagnosticCode.LxssResolutionFailed,
                    RunPhase.Validation,
                    RunEventLevel.Error,
                    "The Lxss mapping returned an unsupported status.")
            };

            return new CollectedValue<LxssProfileResolution>(resolution, updatedDiagnostics);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return new CollectedValue<LxssProfileResolution>(
                new LxssProfileResolution(
                    LxssResolutionStatus.Failed,
                    profile.DistroName,
                    null,
                    null),
                AddDiagnostic(
                    diagnostics,
                    WorkflowDiagnosticCode.LxssResolutionFailed,
                    RunPhase.Validation,
                    RunEventLevel.Error,
                    "The Lxss mapping could not be resolved."));
        }
    }

    private async Task<CollectedValue<VhdxInspectionResult>> InspectAsync(
        string vhdxPath,
        ImmutableArray<WorkflowDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        try
        {
            var inspection = await _vhdxInspector
                .InspectAsync(vhdxPath, cancellationToken)
                .ConfigureAwait(false);
            var updatedDiagnostics = AddInspectionDiagnostics(diagnostics, inspection);

            return new CollectedValue<VhdxInspectionResult>(inspection, updatedDiagnostics);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return new CollectedValue<VhdxInspectionResult>(
                new VhdxInspectionResult(VhdxInspectionStatus.Failed, null),
                AddDiagnostic(
                    diagnostics,
                    WorkflowDiagnosticCode.VhdxInspectionFailed,
                    RunPhase.Snapshot,
                    RunEventLevel.Error,
                    "The VHDX could not be inspected."));
        }
    }

    private async Task<WorkflowResult> CompleteAsync(
        OperationRequest request,
        DateTimeOffset startedAtUtc,
        PreflightReport report,
        ImmutableArray<WorkflowDiagnostic> diagnostics,
        RunJournalAccessMode journalAccessMode,
        bool canUseJournal,
        CancellationToken cancellationToken)
    {
        string? runDirectory = null;
        var journalOpened = false;

        if (canUseJournal)
        {
            var openResult = journalAccessMode == RunJournalAccessMode.Create
                ? await TryCreateRunAsync(request.RunId, cancellationToken).ConfigureAwait(false)
                : await TryOpenExistingRunAsync(request.RunId, cancellationToken).ConfigureAwait(false);
            journalOpened = openResult.Succeeded;
            runDirectory = openResult.RunDirectory;

            if (!journalOpened)
            {
                diagnostics = AddJournalFailureDiagnostic(diagnostics);
            }
            else
            {
                foreach (var eventDraft in CreateEvidenceEvents(
                             request.RunId,
                             request.Profile.DistroName,
                             report,
                             diagnostics))
                {
                    if (await TryAppendAsync(eventDraft, cancellationToken).ConfigureAwait(false))
                    {
                        continue;
                    }

                    diagnostics = AddJournalFailureDiagnostic(diagnostics);
                    break;
                }
            }
        }

        var summary = CreateSummary(request, startedAtUtc, report, diagnostics);
        var shouldWriteSummary = journalAccessMode == RunJournalAccessMode.Create || HasErrors(diagnostics);

        if (journalOpened && shouldWriteSummary &&
            !await TryWriteSummaryAsync(summary, cancellationToken).ConfigureAwait(false))
        {
            diagnostics = AddJournalFailureDiagnostic(diagnostics);
            summary = CreateSummary(request, startedAtUtc, report, diagnostics);
        }

        return new WorkflowResult(summary, report, diagnostics, runDirectory);
    }

    private static ImmutableArray<WorkflowDiagnostic> CreateRequestDiagnostics(
        OperationRequest request,
        ValidationResult validation,
        RunJournalAccessMode journalAccessMode)
    {
        var diagnostics = ImmutableArray<WorkflowDiagnostic>.Empty;

        if (request.RunId == Guid.Empty)
        {
            diagnostics = AddDiagnostic(
                diagnostics,
                WorkflowDiagnosticCode.RequestInvalid,
                RunPhase.Validation,
                RunEventLevel.Error,
                "The run identifier must not be empty.");
        }

        if (!Enum.IsDefined(request.Intent))
        {
            diagnostics = AddDiagnostic(
                diagnostics,
                WorkflowDiagnosticCode.RequestInvalid,
                RunPhase.Validation,
                RunEventLevel.Error,
                "The operation intent is not supported.");
        }

        if (!Enum.IsDefined(journalAccessMode))
        {
            diagnostics = AddDiagnostic(
                diagnostics,
                WorkflowDiagnosticCode.RequestInvalid,
                RunPhase.Validation,
                RunEventLevel.Error,
                "The run journal access mode is not supported.");
        }

        if (journalAccessMode == RunJournalAccessMode.OpenExisting && request.Intent != OperationIntent.Compact)
        {
            diagnostics = AddDiagnostic(
                diagnostics,
                WorkflowDiagnosticCode.RequestInvalid,
                RunPhase.Validation,
                RunEventLevel.Error,
                "An existing run journal can only be opened for a compact operation.");
        }

        foreach (var error in validation.Errors)
        {
            diagnostics = AddDiagnostic(
                diagnostics,
                WorkflowDiagnosticCode.ProfileValidationFailed,
                RunPhase.Validation,
                RunEventLevel.Error,
                error.Message);
        }

        return diagnostics;
    }

    private static ImmutableArray<WorkflowDiagnostic> AddInspectionDiagnostics(
        ImmutableArray<WorkflowDiagnostic> diagnostics,
        VhdxInspectionResult inspection)
    {
        switch (inspection.Status)
        {
            case VhdxInspectionStatus.Succeeded when inspection.Snapshot is null:
                return AddDiagnostic(
                    diagnostics,
                    WorkflowDiagnosticCode.VhdxInspectionFailed,
                    RunPhase.Snapshot,
                    RunEventLevel.Error,
                    "The VHDX inspection did not include a snapshot.");
            case VhdxInspectionStatus.Succeeded when inspection.Snapshot.IsSparse is null:
                return AddDiagnostic(
                    diagnostics,
                    WorkflowDiagnosticCode.SparseStateUnknown,
                    RunPhase.Snapshot,
                    RunEventLevel.Warning,
                    "The VHDX sparse state could not be determined.");
            case VhdxInspectionStatus.Succeeded:
                return diagnostics;
            case VhdxInspectionStatus.Missing:
                return AddDiagnostic(
                    diagnostics,
                    WorkflowDiagnosticCode.VhdxMissing,
                    RunPhase.Snapshot,
                    RunEventLevel.Error,
                    "The requested VHDX file was not found.");
            case VhdxInspectionStatus.Failed:
                return AddDiagnostic(
                    diagnostics,
                    WorkflowDiagnosticCode.VhdxInspectionFailed,
                    RunPhase.Snapshot,
                    RunEventLevel.Error,
                    "The VHDX could not be inspected.");
            default:
                return AddDiagnostic(
                    diagnostics,
                    WorkflowDiagnosticCode.VhdxInspectionFailed,
                    RunPhase.Snapshot,
                    RunEventLevel.Error,
                    "The VHDX inspection returned an unsupported status.");
        }
    }

    private IEnumerable<RunEventDraft> CreateEvidenceEvents(
        Guid runId,
        string requestedDistroName,
        PreflightReport report,
        ImmutableArray<WorkflowDiagnostic> diagnostics)
    {
        yield return new RunEventDraft(
            _clock.UtcNow,
            runId,
            RunPhase.Validation,
            report.Validation.IsValid ? RunEventLevel.Information : RunEventLevel.Error,
            "Profile validation",
            ImmutableArray<string>.Empty,
            null,
            null,
            report.Validation.IsValid ? "Profile validation succeeded." : "Profile validation failed.");

        if (report.InstalledInventory is not null)
        {
            yield return new RunEventDraft(
                report.InstalledInventory.CapturedAtUtc,
                runId,
                RunPhase.Inventory,
                RunEventLevel.Information,
                "WSL installed inventory",
                report.InstalledInventory.Distributions.Select(static distribution => distribution.Name).ToImmutableArray(),
                null,
                null,
                "Installed WSL distributions were collected.");
        }

        if (report.LxssResolution is not null)
        {
            yield return new RunEventDraft(
                _clock.UtcNow,
                runId,
                RunPhase.Validation,
                report.LxssResolution.HasStrictMatchFor(requestedDistroName)
                    ? RunEventLevel.Information
                    : RunEventLevel.Error,
                "Lxss profile mapping",
                CreateMappingArguments(report.LxssResolution),
                null,
                null,
                "The Lxss profile mapping was evaluated.");
        }

        if (report.VhdxInspection is not null)
        {
            yield return new RunEventDraft(
                report.VhdxInspection.Snapshot?.CapturedAtUtc ?? _clock.UtcNow,
                runId,
                RunPhase.Snapshot,
                report.VhdxInspection.Status == VhdxInspectionStatus.Succeeded
                    ? RunEventLevel.Information
                    : RunEventLevel.Error,
                "VHDX snapshot",
                ImmutableArray<string>.Empty,
                null,
                null,
                "The VHDX snapshot was evaluated.");
        }

        if (report.RunningInventory is not null)
        {
            yield return new RunEventDraft(
                report.RunningInventory.CapturedAtUtc,
                runId,
                RunPhase.Inventory,
                RunEventLevel.Information,
                "WSL running inventory",
                report.RunningInventory.Distributions.Select(static distribution => distribution.Name).ToImmutableArray(),
                null,
                null,
                "Running WSL distributions were collected.");
        }

        foreach (var diagnostic in diagnostics)
        {
            yield return new RunEventDraft(
                _clock.UtcNow,
                runId,
                diagnostic.Phase,
                diagnostic.Level,
                "Preflight diagnostic",
                ImmutableArray<string>.Empty,
                null,
                null,
                diagnostic.Message);
        }
    }

    private static ImmutableArray<string> CreateMappingArguments(LxssProfileResolution resolution)
    {
        var arguments = ImmutableArray<string>.Empty;

        if (!string.IsNullOrEmpty(resolution.NormalizedRequestedVhdxPath))
        {
            arguments = arguments.Add(resolution.NormalizedRequestedVhdxPath);
        }

        if (!string.IsNullOrEmpty(resolution.ResolvedVhdxPath))
        {
            arguments = arguments.Add(resolution.ResolvedVhdxPath);
        }

        return arguments;
    }

    private RunSummary CreateSummary(
        OperationRequest request,
        DateTimeOffset startedAtUtc,
        PreflightReport report,
        ImmutableArray<WorkflowDiagnostic> diagnostics) => new(
        request.RunId,
        request.Profile,
        request.Intent,
        startedAtUtc,
        _clock.UtcNow,
        report.VhdxInspection?.Snapshot,
        null,
        HasErrors(diagnostics) ? TerminalResult.ValidationFailed : TerminalResult.Succeeded);

    private async Task<JournalOperationResult> TryCreateRunAsync(
        Guid runId,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _runJournal.CreateRunAsync(runId, cancellationToken).ConfigureAwait(false)
                ?? JournalOperationResult.Failure();
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

    private async Task<JournalOperationResult> TryOpenExistingRunAsync(
        Guid runId,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _runJournal.OpenExistingRunAsync(runId, cancellationToken).ConfigureAwait(false)
                ?? JournalOperationResult.Failure();
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

    private async Task<bool> TryAppendAsync(RunEventDraft eventDraft, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _runJournal.AppendAsync(eventDraft, cancellationToken).ConfigureAwait(false);
            return result?.Succeeded == true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private async Task<bool> TryWriteSummaryAsync(RunSummary summary, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _runJournal.WriteSummaryAsync(summary, cancellationToken).ConfigureAwait(false);
            return result?.Succeeded == true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return false;
        }
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
            : AddDiagnostic(
                diagnostics,
                WorkflowDiagnosticCode.JournalFailure,
                RunPhase.Failed,
                RunEventLevel.Error,
                "Run diagnostics could not be persisted.");

    private static ImmutableArray<WorkflowDiagnostic> AddDiagnostic(
        ImmutableArray<WorkflowDiagnostic> diagnostics,
        WorkflowDiagnosticCode code,
        RunPhase phase,
        RunEventLevel level,
        string message) => diagnostics.Add(new WorkflowDiagnostic(code, phase, level, message));

    private sealed record CollectedValue<T>(T Value, ImmutableArray<WorkflowDiagnostic> Diagnostics);
}
