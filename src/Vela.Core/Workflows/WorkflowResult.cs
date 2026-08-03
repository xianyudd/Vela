using System.Collections.Immutable;
using Vela.Core.Contracts;
using Vela.Core.Models;
using Vela.Core.Validation;

namespace Vela.Core.Workflows;

public sealed record PreflightReport(
    ValidationResult Validation,
    WslInventory? InstalledInventory,
    LxssProfileResolution? LxssResolution,
    VhdxInspectionResult? VhdxInspection,
    WslInventory? RunningInventory);

public sealed record WorkflowDiagnostic(
    WorkflowDiagnosticCode Code,
    RunPhase Phase,
    RunEventLevel Level,
    string Message);

public enum WorkflowDiagnosticCode
{
    RequestInvalid,
    ProfileValidationFailed,
    InstalledInventoryFailed,
    DistroNotInstalled,
    LxssResolutionNotFound,
    LxssResolutionFailed,
    LxssMappingMismatch,
    VhdxMissing,
    VhdxInspectionFailed,
    SparseStateUnknown,
    RunningInventoryFailed,
    ShutdownTimedOut,
    DiskPartPreflightFailed,
    DiskPartCompactFailed,
    JournalFailure
}

public enum RunJournalAccessMode
{
    Create,
    OpenExisting
}

public sealed record WorkflowResult(
    RunSummary Summary,
    PreflightReport Preflight,
    ImmutableArray<WorkflowDiagnostic> Diagnostics,
    string? RunDirectory)
{
    public bool IsSuccessful => Summary.TerminalResult is TerminalResult.Succeeded or TerminalResult.CompletedWithNoReclaim;
}
