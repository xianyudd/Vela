namespace Vela.Windows.Security;

/// <summary>
/// Disposable scope around <c>SeSecurityPrivilege</c>. Acquires the privilege
/// through <see cref="INativeSecurityAdapter"/> and strictly restores the
/// previous state when disposed — including exception paths.
/// </summary>
public sealed class WindowsTokenPrivilegeScope : IDisposable
{
    private readonly INativeSecurityAdapter _adapter;
    private bool _disposed;

    private WindowsTokenPrivilegeScope(INativeSecurityAdapter adapter, IDisposable inner)
    {
        _adapter = adapter;
        Inner = inner;
    }

    internal IDisposable Inner { get; }

    public static WindowsTokenPrivilegeScope Acquire(INativeSecurityAdapter adapter)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        var inner = adapter.AcquireSecurityPrivilegeScope();
        return new WindowsTokenPrivilegeScope(adapter, inner);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Inner.Dispose();
    }
}
