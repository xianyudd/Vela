namespace Vela.Application.Display;

/// <summary>
/// Display-safe summary of a VHDX target. Raw paths are not exposed; every
/// field is already a bounded, localized display string.
/// </summary>
public sealed record DisplayVhdxSummary(
    string FileName,
    string FileType,
    string CurrentSize,
    string MappingStatus,
    string SparseState,
    string HostCapacityStatus);