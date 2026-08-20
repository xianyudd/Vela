namespace Vela.Application.Display;

/// <summary>
/// Display-safe summary of a VHDX target. Raw paths are not exposed.
/// </summary>
public sealed record DisplayVhdxSummary(
    string DistroName,
    string DisplayName,
    bool TargetConfigured,
    long? CurrentVhdxSizeBytes,
    long? ReclaimableBytes);
