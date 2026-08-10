namespace Vela.Core.Contracts;

public interface ICompactionImpactEstimator
{
    Task<CompactionImpactEstimate> EstimateAsync(
        string distroName,
        string vhdxPath,
        long currentVhdxSizeBytes,
        CancellationToken cancellationToken);
}

public sealed record CompactionImpactEstimate(
    CompactionImpactStatus Status,
    long? CurrentVhdxSizeBytes,
    long? UsedBytes,
    long? ReclaimableBytes,
    string Message);

public enum CompactionImpactStatus
{
    Estimated,
    Unavailable,
    Failed
}
