using Vela.Core.Models;

namespace Vela.Core.Contracts;

public sealed record OperationRequestWriteResult(
    bool Succeeded,
    string? RequestPath)
{
    public static OperationRequestWriteResult Success(string requestPath) =>
        new(true, requestPath);

    public static OperationRequestWriteResult Failure() =>
        new(false, null);
}

public sealed record OperationRequestReadResult(
    bool Succeeded,
    OperationRequest? Request,
    string? SourcePath)
{
    public static OperationRequestReadResult Success(
        OperationRequest request,
        string sourcePath) =>
        new(true, request, sourcePath);

    public static OperationRequestReadResult Failure() =>
        new(false, null, null);
}

public sealed record OperationRequestClaimResult(
    bool Succeeded,
    OperationRequest? Request,
    string? SourcePath)
{
    public static OperationRequestClaimResult Success(
        OperationRequest request,
        string sourcePath) =>
        new(true, request, sourcePath);

    public static OperationRequestClaimResult Failure() =>
        new(false, null, null);
}

public sealed record OperationRequestConsumeResult(bool Succeeded)
{
    public static OperationRequestConsumeResult Success() => new(true);

    public static OperationRequestConsumeResult Failure() => new(false);
}

public interface IOperationRequestStore
{
    Task<OperationRequestWriteResult> WriteAsync(
        OperationRequest request,
        CancellationToken cancellationToken);

    Task<OperationRequestReadResult> ReadAsync(
        Guid expectedRunId,
        CancellationToken cancellationToken);

    async Task<OperationRequestClaimResult> ClaimAsync(
        Guid expectedRunId,
        CancellationToken cancellationToken)
    {
        var read = await ReadAsync(expectedRunId, cancellationToken).ConfigureAwait(false);
        return read.Succeeded && read.Request is not null && read.SourcePath is not null
            ? OperationRequestClaimResult.Success(read.Request, read.SourcePath)
            : OperationRequestClaimResult.Failure();
    }

    Task<OperationRequestConsumeResult> ConsumeAsync(
        Guid expectedRunId,
        CancellationToken cancellationToken);
}

public enum ElevatedWorkerLaunchStatus
{
    Started,
    Cancelled,
    Failed,
    Rejected
}

/// <summary>
/// Outcome of a worker launch attempt. <paramref name="FailureReason"/> is a
/// short, path-free sentence recorded in the run journal when the launch failed
/// for a reason worth telling the operator; it is null for a successful launch
/// and for failures that carry no useful detail.
/// </summary>
public sealed record ElevatedWorkerLaunchResult(
    ElevatedWorkerLaunchStatus Status,
    string? FailureReason = null);

public interface IElevatedWorkerLauncher
{
    Task<ElevatedWorkerLaunchResult> LaunchAsync(
        Guid runId,
        CancellationToken cancellationToken);
}
