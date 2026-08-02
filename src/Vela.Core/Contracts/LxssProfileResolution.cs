namespace Vela.Core.Contracts;

public sealed record LxssProfileResolution(
    LxssResolutionStatus Status,
    string DistroName,
    string? ResolvedVhdxPath,
    string? NormalizedRequestedVhdxPath)
{
    public bool HasStrictPathMatch =>
        Status == LxssResolutionStatus.Matched &&
        !string.IsNullOrWhiteSpace(ResolvedVhdxPath) &&
        !string.IsNullOrWhiteSpace(NormalizedRequestedVhdxPath) &&
        string.Equals(ResolvedVhdxPath, NormalizedRequestedVhdxPath, StringComparison.Ordinal);

    public bool HasStrictMatchFor(string requestedDistroName) =>
        !string.IsNullOrWhiteSpace(requestedDistroName) &&
        string.Equals(DistroName, requestedDistroName, StringComparison.Ordinal) &&
        HasStrictPathMatch;
}

public enum LxssResolutionStatus
{
    Matched,
    Mismatched,
    NotFound,
    Failed
}
