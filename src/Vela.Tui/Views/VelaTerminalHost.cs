using Vela.Core.Models;
using Vela.Tui.Application;

namespace Vela.Tui.Views;

public sealed class VelaTerminalHost : IDisposable
{
    // Short enough that the elapsed-seconds counter visibly moves while a slow
    // wsl.exe inventory is still running, long enough to stay free.
    private static readonly TimeSpan DefaultProgressInterval = TimeSpan.FromSeconds(1);

    private readonly VelaTerminalShell _shell;
    private readonly AutomaticPreflightCoordinator _preflight;
    private readonly ITuiDispatcher _dispatcher;
    private readonly TimeSpan _progressInterval;
    private CancellationTokenSource? _progressCancellation;
    private int _disposed;

    public VelaTerminalHost(
        VelaTerminalShell shell,
        AutomaticPreflightCoordinator preflight,
        ITuiDispatcher dispatcher,
        TimeSpan? progressInterval = null)
    {
        _shell = shell ?? throw new ArgumentNullException(nameof(shell));
        _preflight = preflight ?? throw new ArgumentNullException(nameof(preflight));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _progressInterval = progressInterval is { } interval && interval > TimeSpan.Zero
            ? interval
            : DefaultProgressInterval;
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

        var running = _preflight.Start(profile);
        // Started afterwards on purpose: the ticker stops as soon as the status
        // is no longer Checking, which is only true once Start has published it.
        StartProgressTicker();
        return running;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _preflight.StateChanged -= OnPreflightStateChanged;
        CancelAndDispose(Interlocked.Exchange(ref _progressCancellation, null));
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
            if (current.Generation != state.Generation || current.Revision != state.Revision)
            {
                return;
            }

            _shell.ApplyPreflight(state);
            if (state.Status == AutomaticPreflightStatus.Checking)
            {
                // ApplyPreflight resets the status line to the navigation hint,
                // so the progress text has to be written after it.
                _shell.ShowStatus(PreflightOverviewFormatter.FormatCheckingStatus(state.Elapsed));
            }
        });
    }

    private void StartProgressTicker()
    {
        var cancellation = new CancellationTokenSource();
        CancelAndDispose(Interlocked.Exchange(ref _progressCancellation, cancellation));
        if (IsDisposed)
        {
            // Dispose ran concurrently; do not let this ticker outlive it.
            CancelAndDispose(Interlocked.Exchange(ref _progressCancellation, null));
            return;
        }

        _ = RunProgressTickerAsync(cancellation.Token);
    }

    private async Task RunProgressTickerAsync(CancellationToken cancellationToken)
    {
        var elapsed = TimeSpan.Zero;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(_progressInterval, cancellationToken).ConfigureAwait(false);
                elapsed += _progressInterval;
                if (IsDisposed || !_preflight.TryRefreshChecking(elapsed))
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
            // The token source was disposed together with this host.
        }
    }

    private static void CancelAndDispose(CancellationTokenSource? cancellation)
    {
        if (cancellation is null)
        {
            return;
        }

        try
        {
            cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        cancellation.Dispose();
    }

    private bool IsDisposed => Volatile.Read(ref _disposed) != 0;
}
