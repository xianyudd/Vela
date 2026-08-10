using System.Collections.Immutable;
using Vela.Core.Contracts;
using Vela.Core.Models;
using Vela.Core.Workflows;
using Vela.Tui.Menu;
using Profile = Vela.Core.Models.Profile;

namespace Vela.Tui.Application;

public enum TargetMappingState
{
    NotChecked,
    Matched,
    Mismatched,
    NotFound,
    Failed
}

public enum TargetInspectionState
{
    NotChecked,
    Available,
    Missing,
    Failed
}

public enum PreflightDataState
{
    NotChecked,
    Available,
    Failed
}

public sealed record VhdxEvidenceViewModel(
    long FileLengthBytes,
    DateTimeOffset LastWriteUtc,
    bool? IsSparse,
    long DriveTotalSizeBytes,
    long DriveAvailableFreeSpaceBytes,
    string? FilePath = null);

public sealed record DashboardViewModel(
    string ApplicationTitle,
    string ProfileTitle,
    string DistroName,
    bool TargetConfigured,
    TargetMappingState MappingState,
    TargetInspectionState InspectionState,
    VhdxEvidenceViewModel? VhdxEvidence,
    ImmutableArray<string> RunningDistros,
    ImmutableArray<string> Notices,
    string? ErrorMessage,
    bool LogsAvailable,
    PreflightDataState RunningInventoryState = PreflightDataState.NotChecked,
    PreflightDataState LogAvailabilityState = PreflightDataState.NotChecked,
    ImmutableArray<WslDistribution> InstalledDistros = default)
{
    public static DashboardViewModel CreateInitial(Profile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        return new DashboardViewModel(
            MainMenu.ApplicationTitle,
            "档案：" + profile.DisplayName,
            profile.DistroName,
            !string.IsNullOrWhiteSpace(profile.VhdxPath),
            TargetMappingState.NotChecked,
            TargetInspectionState.NotChecked,
            null,
            ImmutableArray<string>.Empty,
            ImmutableArray<string>.Empty,
            null,
            LogsAvailable: false,
            RunningInventoryState: PreflightDataState.NotChecked,
            LogAvailabilityState: PreflightDataState.NotChecked,
            InstalledDistros: ImmutableArray<WslDistribution>.Empty);
    }

    public static DashboardViewModel FromWorkflow(WorkflowResult workflowResult)
    {
        ArgumentNullException.ThrowIfNull(workflowResult);

        var preflight = workflowResult.Preflight;
        var runningDistros = preflight.RunningInventory?.Distributions
            .Where(static distribution => distribution.State == WslDistributionState.Running)
            .Select(static distribution => distribution.Name)
            .ToImmutableArray() ?? ImmutableArray<string>.Empty;
        var installedDistros = preflight.InstalledInventory?.Distributions
            .Where(static distribution => !string.IsNullOrWhiteSpace(distribution.Name))
            .ToImmutableArray() ?? ImmutableArray<WslDistribution>.Empty;
        var notices = workflowResult.Diagnostics
            .Where(static diagnostic => diagnostic.Level is RunEventLevel.Trace or RunEventLevel.Information or RunEventLevel.Warning)
            .Select(static diagnostic => TuiDisplayText.LabelForDiagnostic(diagnostic.Code))
            .Distinct(StringComparer.Ordinal)
            .ToImmutableArray();
        var error = workflowResult.Diagnostics
            .FirstOrDefault(static diagnostic => diagnostic.Level == RunEventLevel.Error);
        var snapshot = preflight.VhdxInspection?.Snapshot;
        var evidence = snapshot is null
            ? null
            : new VhdxEvidenceViewModel(
                snapshot.FileLengthBytes,
                snapshot.LastWriteUtc,
                snapshot.IsSparse,
                snapshot.Drive.TotalSizeBytes,
                snapshot.Drive.AvailableFreeSpaceBytes,
                snapshot.Path);
        var runningInventoryState = preflight.RunningInventory is not null
            ? PreflightDataState.Available
            : HasDiagnostic(
                workflowResult.Diagnostics,
                WorkflowDiagnosticCode.RunningInventoryFailed)
                ? PreflightDataState.Failed
                : PreflightDataState.NotChecked;
        var logAvailabilityState = !string.IsNullOrWhiteSpace(workflowResult.RunDirectory)
            ? PreflightDataState.Available
            : HasDiagnostic(
                workflowResult.Diagnostics,
                WorkflowDiagnosticCode.JournalFailure)
                ? PreflightDataState.Failed
                : PreflightDataState.NotChecked;

        return new DashboardViewModel(
            MainMenu.ApplicationTitle,
            "档案：" + workflowResult.Summary.Profile.DisplayName,
            workflowResult.Summary.Profile.DistroName,
            !string.IsNullOrWhiteSpace(workflowResult.Summary.Profile.VhdxPath),
            MapMappingState(preflight.LxssResolution?.Status, workflowResult.Diagnostics),
            MapInspectionState(preflight.VhdxInspection?.Status),
            evidence,
            runningDistros,
            notices,
            error is null ? null : TuiDisplayText.LabelForDiagnostic(error.Code),
            !string.IsNullOrWhiteSpace(workflowResult.RunDirectory),
            runningInventoryState,
            logAvailabilityState,
            installedDistros);
    }

    private static TargetMappingState MapMappingState(
        LxssResolutionStatus? status,
        ImmutableArray<WorkflowDiagnostic> diagnostics) => status switch
    {
        null when HasDiagnostic(
            diagnostics,
            WorkflowDiagnosticCode.InstalledInventoryFailed) => TargetMappingState.Failed,
        null => TargetMappingState.NotChecked,
        LxssResolutionStatus.Matched => TargetMappingState.Matched,
        LxssResolutionStatus.Mismatched => TargetMappingState.Mismatched,
        LxssResolutionStatus.NotFound => TargetMappingState.NotFound,
        _ => TargetMappingState.Failed
    };

    private static TargetInspectionState MapInspectionState(VhdxInspectionStatus? status) => status switch
    {
        null => TargetInspectionState.NotChecked,
        VhdxInspectionStatus.Succeeded => TargetInspectionState.Available,
        VhdxInspectionStatus.Missing => TargetInspectionState.Missing,
        _ => TargetInspectionState.Failed
    };

    private static bool HasDiagnostic(
        ImmutableArray<WorkflowDiagnostic> diagnostics,
        WorkflowDiagnosticCode code) =>
        diagnostics.Any(diagnostic => diagnostic.Code == code);
}

public interface IPreflightViewModelSource
{
    Task<DashboardViewModel> CreateAsync(
        Profile profile,
        CancellationToken cancellationToken = default);
}

public sealed class WorkflowPreflightViewModelSource : IPreflightViewModelSource
{
    private readonly PreflightWorkflow _workflow;

    public WorkflowPreflightViewModelSource(PreflightWorkflow workflow)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        _workflow = workflow;
    }

    public async Task<DashboardViewModel> CreateAsync(
        Profile profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var request = new OperationRequest(Guid.NewGuid(), profile, OperationIntent.Preflight);
        var result = await _workflow.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
        return DashboardViewModel.FromWorkflow(result);
    }
}
