using System.Collections.Immutable;
using Vela.Application.Display;
using Vela.Core.Models;

namespace Vela.Application.Tui;

/// <summary>
/// Sole mapper from trusted session state to immutable display projection.
/// The view layer never receives <see cref="TuiSessionState"/> or any
/// trusted types.
/// </summary>
public static class TuiViewProjector
{
    /// <summary>
    /// Projects trusted state to a display-safe view state.
    /// </summary>
    public static TuiViewState Project(TuiSessionState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        var page = ResolvePage(state);
        var statusMessage = ResolveStatusMessage(state);
        var statusSeverity = ResolveSeverity(state);
        var isBusy = IsBusy(state);

        return new TuiViewState(
            page,
            Title: "Vela — WSL VHDX Compact",
            StatusMessage: statusMessage,
            StatusSeverity: statusSeverity,
            SelectedIndex: ResolveSelectedIndex(page, state),
            TargetSummaries: ProjectTargets(state),
            RunHistory: state.RunHistoryEntries,
            LogEvents: state.LogDetailEvents,
            IsBusy: isBusy,
            ErrorMessage: ResolveError(state));
    }

    private static TuiWorkspacePage ResolvePage(TuiSessionState state)
    {
        if (state.StartupStatus is StartupStatus.Idle or StartupStatus.Confirming or StartupStatus.Initializing)
        {
            return TuiWorkspacePage.StartupConfirmation;
        }

        if (state.CompactionStatus is CompactionStatus.Launching or CompactionStatus.Running)
        {
            return TuiWorkspacePage.Execution;
        }

        if (state.ImpactStatus != ImpactPreviewStatus.Idle)
        {
            return TuiWorkspacePage.ImpactPreview;
        }

        if (state.PreflightStatus != PreflightStatus.Idle)
        {
            return TuiWorkspacePage.Preflight;
        }

        return TuiWorkspacePage.Dashboard;
    }

    private static string ResolveStatusMessage(TuiSessionState state)
    {
        if (state.StartupStatus == StartupStatus.Confirming)
        {
            return "正在输入确认";
        }

        if (state.CompactionStatus == CompactionStatus.Running)
        {
            return "压缩执行中";
        }

        if (state.ImpactStatus == ImpactPreviewStatus.Estimating)
        {
            return "正在评估可回收空间";
        }

        if (state.PreflightStatus == PreflightStatus.Checking)
        {
            return "正在只读预检";
        }

        return "就绪";
    }

    private static DisplayMessageSeverity ResolveSeverity(TuiSessionState state)
    {
        if (state.PreflightStatus == PreflightStatus.Failed ||
            state.ImpactStatus == ImpactPreviewStatus.Failed ||
            state.StartupStatus == StartupStatus.Failed)
        {
            return DisplayMessageSeverity.Error;
        }

        if (state.PreflightStatus == PreflightStatus.Attention)
        {
            return DisplayMessageSeverity.Warning;
        }

        return DisplayMessageSeverity.Info;
    }

    private static int ResolveSelectedIndex(TuiWorkspacePage page, TuiSessionState state) =>
        page == TuiWorkspacePage.Logs
            ? state.SelectedLogIndex
            : state.SelectedProfileIndex;

    private static ImmutableArray<DisplayVhdxSummary> ProjectTargets(TuiSessionState state)
    {
        if (state.LockedTarget is null)
        {
            return ImmutableArray<DisplayVhdxSummary>.Empty;
        }

        var target = state.LockedTarget;
        return ImmutableArray.Create(
            new DisplayVhdxSummary(
                target.Profile.DistroName,
                target.Profile.DisplayName,
                TargetConfigured: !string.IsNullOrWhiteSpace(target.VhdxPath),
                CurrentVhdxSizeBytes: null,
                ReclaimableBytes: state.LastImpactEstimate?.ReclaimableBytes));
    }

    private static string? ResolveError(TuiSessionState state)
    {
        if (state.StartupStatus == StartupStatus.Failed)
        {
            return "启动初始化失败。";
        }

        if (state.PreflightStatus == PreflightStatus.Failed && state.LastPreflightError is { } error)
        {
            return error.ErrorMessage;
        }

        if (state.ImpactStatus == ImpactPreviewStatus.Failed && state.LastImpactError is { } impactError)
        {
            return impactError.Text;
        }

        if (state.RunHistoryError is { } historyError)
        {
            return historyError.Text;
        }

        return null;
    }

    private static bool IsBusy(TuiSessionState state) =>
        state.StartupStatus == StartupStatus.Initializing ||
        state.PreflightStatus == PreflightStatus.Checking ||
        state.ImpactStatus == ImpactPreviewStatus.Estimating ||
        state.CompactionStatus is CompactionStatus.Launching or CompactionStatus.Running;
}
