namespace Vela.Windows.DiskPart;

/// <summary>
/// Creates a trusted, privileged-ACL-protected DiskPart script and returns a
/// live lease through which the script file remains pinned.
/// </summary>
public interface IPrivilegedDiskPartWorkspace
{
    Task<IPrivilegedDiskPartScriptLease> CreateScriptAsync(
        Guid runId,
        string script,
        CancellationToken cancellationToken);
}

/// <summary>
/// A live lease over a privileged DiskPart script. The pinned handle must be
/// released only when <see cref="IAsyncDisposable.DisposeAsync"/> runs. The
/// implementation must verify the underlying file identity and security
/// descriptor before re-exposing <see cref="ScriptPath"/> to the caller.
/// </summary>
public interface IPrivilegedDiskPartScriptLease : IAsyncDisposable
{
    string ScriptPath { get; }

    ValueTask VerifyAsync(CancellationToken cancellationToken);
}
