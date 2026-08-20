using Microsoft.Win32.SafeHandles;

namespace Vela.Windows.Security;

/// <summary>
/// Stable identity metadata captured from a handle, used to detect replacement
/// attacks between the creation handle and any later reopen.
/// </summary>
public sealed record FileIdentity(
    long VolumeSerialNumber,
    long FileIndex,
    long FileLength,
    long LastWriteTimeUtcTicks);
