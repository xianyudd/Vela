using Vela.Core.Contracts;

namespace Vela.Windows.Storage;

/// <summary>
/// Tests the one condition that decides whether <c>compact vdisk</c> can run:
/// can the file be opened with no sharing granted to anyone else? diskpart needs
/// an exclusive handle, so an exclusive open here succeeds exactly when diskpart
/// would also get one. The probe never writes, and releases the handle at once.
/// </summary>
public sealed class VhdxHandleProbe : IVhdxHandleProbe
{
    // The two Win32 codes that mean "another holder has this file" rather than
    // "the call went wrong". Only these justify reporting Held, because Held is
    // the one answer that stops a run.
    private const int ErrorSharingViolation = 32;
    private const int ErrorLockViolation = 33;

    public Task<VhdxHandleState> ProbeAsync(string vhdxPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Probe(vhdxPath));
    }

    private static VhdxHandleState Probe(string vhdxPath)
    {
        if (!IsProbeablePath(vhdxPath))
        {
            return VhdxHandleState.Unknown;
        }

        try
        {
            // Read access is enough; FileShare.None is what makes this a lock
            // test rather than a read.
            using var stream = new FileStream(
                vhdxPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.None);
            return VhdxHandleState.Free;
        }
        catch (IOException exception) when (IsHolderConflict(exception))
        {
            return VhdxHandleState.Held;
        }
        catch (Exception)
        {
            // A missing file, an ACL refusal or anything else is not evidence
            // that a holder exists, so it must never read as Held.
            return VhdxHandleState.Unknown;
        }
    }

    // FileNotFoundException and friends also derive from IOException, so the
    // code has to be inspected rather than the exception type.
    private static bool IsHolderConflict(IOException exception) =>
        (exception.HResult & 0xFFFF) is ErrorSharingViolation or ErrorLockViolation;

    private static bool IsProbeablePath(string? path) =>
        !string.IsNullOrWhiteSpace(path) &&
        !path.Any(char.IsControl) &&
        Path.IsPathFullyQualified(path) &&
        path.EndsWith(".vhdx", StringComparison.OrdinalIgnoreCase);
}
