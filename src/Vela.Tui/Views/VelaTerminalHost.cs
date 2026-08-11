using Vela.Core.Models;
using Vela.Tui.Application;

namespace Vela.Tui.Views;

public sealed class VelaTerminalHost : IDisposable
{
    private readonly VelaTerminalShell _shell;
    private readonly AutomaticPreflightCoordinator _preflight;
    private readonly ITuiDispatcher _dispatcher;
    private int _disposed;

    public VelaTerminalHost(
        VelaTerminalShell shell,
        AutomaticPreflightCoordinator preflight,
        ITuiDispatcher dispatcher)
    {
        _shell = shell ?? throw new ArgumentNullException(nameof(shell));
        _preflight = preflight ?? throw new ArgumentNullException(nameof(preflight));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _preflight.StateChanged += OnPreflightStateChanged;
    }

    public Task Start(Profile profile, bool preserveTargetSelection = false)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        if (preserveTargetSelection)
        {
            _shell.PrepareTargetPreflight(profile);
        }
        else
        {
            _shell.SetCurrentProfile(profile);
        }

        return _preflight.Start(profile);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _preflight.StateChanged -= OnPreflightStateChanged;
    }

    private void OnPreflightStateChanged(AutomaticPreflightState state)
    {
        _dispatcher.Post(() =>
        {
            if (IsDisposed)
            {
                return;
            }

            var current = _preflight.Current;
            if (current.Generation == state.Generation && current.Revision == state.Revision)
            {
                _shell.ApplyPreflight(state);
            }
        });
    }

    private bool IsDisposed => Volatile.Read(ref _disposed) != 0;
}
