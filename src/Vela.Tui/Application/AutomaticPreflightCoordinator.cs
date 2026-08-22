using System.Globalization;
using Vela.Core.Models;

namespace Vela.Tui.Application;

public enum AutomaticPreflightStatus
{
    Idle,
    Checking,
    Ready,
    Attention,
    Failed,
    Stale
}

/// <param name="Elapsed">
/// How long the in-flight check has been running, refreshed while the status is
/// <see cref="AutomaticPreflightStatus.Checking"/>. Null means "not tracked",
/// which is what every terminal state carries.
/// </param>
public sealed record AutomaticPreflightState(
    Guid ProfileId,
    long Generation,
    long Revision,
    AutomaticPreflightStatus Status,
    DashboardViewModel? Dashboard,
    string? Message,
    TimeSpan? Elapsed = null)
{
    public static AutomaticPreflightState Idle { get; } = new(
        Guid.Empty,
        Generation: 0,
        Revision: 0,
        AutomaticPreflightStatus.Idle,
        Dashboard: null,
        Message: "预检尚未运行。");

    public bool CanExecuteCompaction => Status == AutomaticPreflightStatus.Ready;
}

public sealed class AutomaticPreflightCoordinator : IDisposable
{
    private const string CheckingMessage = "正在进行只读预检。";

    private readonly Func<Profile, CancellationToken, Task<DashboardViewModel>> _createDashboardAsync;
    private readonly object _sync = new();
    private CancellationTokenSource? _activeCancellation;
    private long _generation;
    private long _revision;
    private AutomaticPreflightState _current = AutomaticPreflightState.Idle;
    private bool _disposed;

    public AutomaticPreflightCoordinator(
        Func<Profile, CancellationToken, Task<DashboardViewModel>> createDashboardAsync)
    {
        _createDashboardAsync = createDashboardAsync ?? throw new ArgumentNullException(nameof(createDashboardAsync));
    }

    public event Action<AutomaticPreflightState>? StateChanged;

    public AutomaticPreflightState Current => GetSnapshot();

    public AutomaticPreflightState GetSnapshot()
    {
        lock (_sync)
        {
            return _current;
        }
    }

    public Task Start(Profile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        long generation;
        CancellationToken cancellationToken;
        AutomaticPreflightState checking;
        lock (_sync)
        {
            ThrowIfDisposed();
            _activeCancellation?.Cancel();
            _activeCancellation?.Dispose();
            _activeCancellation = new CancellationTokenSource();
            generation = ++_generation;
            cancellationToken = _activeCancellation.Token;
            checking = new AutomaticPreflightState(
                profile.Id,
                generation,
                ++_revision,
                AutomaticPreflightStatus.Checking,
                Dashboard: null,
                CheckingMessage,
                Elapsed: TimeSpan.Zero);
            _current = checking;
        }

        Notify(checking);
        return CompleteAsync(profile, generation, cancellationToken);
    }

    public void MarkStale(Profile profile, string message)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        AutomaticPreflightState stale;
        lock (_sync)
        {
            ThrowIfDisposed();
            if (_current.ProfileId != profile.Id)
            {
                return;
            }

            _activeCancellation?.Cancel();
            _activeCancellation?.Dispose();
            _activeCancellation = null;
            stale = new AutomaticPreflightState(
                profile.Id,
                ++_generation,
                ++_revision,
                AutomaticPreflightStatus.Stale,
                _current.Dashboard,
                message);
            _current = stale;
        }

        Notify(stale);
    }

    /// <summary>
    /// Republishes the in-flight check with a refreshed elapsed time so the shell
    /// can show that a slow read-only preflight is still alive rather than wedged.
    /// Returns false when no check is in flight, which is the caller's stop signal.
    /// </summary>
    public bool TryRefreshChecking(TimeSpan elapsed)
    {
        AutomaticPreflightState next;
        lock (_sync)
        {
            // Never throws when disposed: the caller is a background ticker that
            // races with shutdown by design and only needs the stop signal.
            if (_disposed || _current.Status != AutomaticPreflightStatus.Checking)
            {
                return false;
            }

            next = _current with
            {
                Revision = ++_revision,
                Message = BuildCheckingMessage(elapsed),
                Elapsed = elapsed < TimeSpan.Zero ? TimeSpan.Zero : elapsed
            };
            _current = next;
        }

        Notify(next);
        return true;
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _activeCancellation?.Cancel();
            _activeCancellation?.Dispose();
            _activeCancellation = null;
            _disposed = true;
        }
    }

    private async Task CompleteAsync(Profile profile, long generation, CancellationToken cancellationToken)
    {
        try
        {
            var dashboard = await _createDashboardAsync(profile, cancellationToken).ConfigureAwait(false);
            var status = dashboard.ErrorMessage is not null || !dashboard.Notices.IsDefaultOrEmpty
                ? AutomaticPreflightStatus.Attention
                : AutomaticPreflightStatus.Ready;
            PublishIfCurrent(
                profile.Id,
                generation,
                status,
                dashboard,
                status == AutomaticPreflightStatus.Ready
                    ? "预检已完成。"
                    : "预检已完成，发现需要关注的问题。");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            PublishIfCurrent(
                profile.Id,
                generation,
                AutomaticPreflightStatus.Failed,
                dashboard: null,
                message: "预检失败，请查看日志后重试。");
        }
    }

    private void PublishIfCurrent(
        Guid profileId,
        long generation,
        AutomaticPreflightStatus status,
        DashboardViewModel? dashboard,
        string message)
    {
        AutomaticPreflightState? next = null;
        lock (_sync)
        {
            if (_disposed || _current.ProfileId != profileId || _current.Generation != generation)
            {
                return;
            }

            next = new AutomaticPreflightState(
                profileId,
                generation,
                ++_revision,
                status,
                dashboard,
                message);
            _current = next;
        }

        Notify(next);
    }

    private static string BuildCheckingMessage(TimeSpan elapsed)
    {
        var seconds = (long)elapsed.TotalSeconds;
        return seconds <= 0
            ? CheckingMessage
            : $"正在进行只读预检（已用 {seconds.ToString(CultureInfo.InvariantCulture)} 秒）。";
    }

    private void Notify(AutomaticPreflightState state)
    {
        var subscribers = StateChanged;
        if (subscribers is null)
        {
            return;
        }

        foreach (var subscriber in subscribers.GetInvocationList().Cast<Action<AutomaticPreflightState>>())
        {
            try
            {
                subscriber(state);
            }
            catch (Exception)
            {
                // State delivery must not alter the preflight lifecycle.
            }
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
