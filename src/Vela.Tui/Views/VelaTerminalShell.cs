using System.Collections.ObjectModel;
using System.Drawing;
using Terminal.Gui.App;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using Vela.Core.Contracts;
using Vela.Core.Models;
using Vela.Tui;
using Vela.Tui.Application;
using Vela.Tui.Menu;
using Vela.Tui.Rendering;

namespace Vela.Tui.Views;

public enum VelaShellLayout { SinglePane, TwoPane }

public enum VelaWorkspacePage { Overview, TargetDetail, Profiles, RecentRuns, Logs, LogAnalysis, ActionPreview, Confirmation, Running, Result }

/// <summary>
/// The interactive shell deliberately owns one navigation list.  Captions are display-only;
/// there is no second focus ring hidden in a wide layout.
/// </summary>
public sealed class VelaTerminalShell : Window
{
    private const int ConfirmationInputLimit = 16;
    private readonly Label _header;
    private readonly Label _modeBadge;
    private readonly FrameView _navigationPanel;
    private readonly FrameView _contentPanel;
    private readonly Label _contentHeading;
    private readonly Label _groupCaptions;
    private readonly ListView _navigation;
    private readonly ObservableCollection<string> _navigationLabels;
    private readonly ListView _logList;
    private readonly TextField _confirmationInput;
    private readonly Label _workspace;
    private readonly PreflightHomeView _homeView;
    private readonly PreflightTargetDetailView _targetDetailView;
    private readonly CompactionImpactView _impactView;
    private readonly Label _decision;
    private readonly FrameView _evidencePanel;
    private readonly Label _evidence;
    private readonly FrameView _actionBar;
    private readonly Label _status;
    private readonly Label _quitHint;
    private readonly IReadOnlyList<MainMenuItem> _menuItems;
    private readonly IReadOnlyList<MainMenuItem> _visibleMenuItems;
    private readonly string _applicationTitle;
    private DashboardViewModel _dashboard;
    private ConfirmationViewModel? _confirmation;
    private int _availablePageRows = 8;
    private int _screenWidth = VelaLayoutMetrics.TwoPaneWidth;
    private int _screenHeight = VelaLayoutMetrics.TwoPaneHeight;
    private string? _pageTitle;
    private string[] _pageLines = [];
    private RunLogLine[] _logEntries = [];
    private RunLogSnapshot? _logSnapshot;
    private RunLogAnalysisViewModel? _logAnalysis;
    private string[] _logLines = [];
    private RunEventLevel[] _logLevels = [];
    private bool _updatingNavigationLabels;
    private bool _selectingLegacyMenuIndex;
    private bool _navigationReady;
    private int _lastPreviewedSelection = -1;
    private long _navigationRevision;
    private int _selectedTargetIndex;
    private bool _targetLocked;
    private string? _lockedTargetName;
    private CompactionImpactEstimate? _compactionEstimate;
    private int? _legacySelectedMenuIndex;

    public VelaTerminalShell(MainMenuViewModel menu, DashboardViewModel dashboard)
    {
        ArgumentNullException.ThrowIfNull(menu);
        _dashboard = dashboard ?? throw new ArgumentNullException(nameof(dashboard));
        _menuItems = menu.Items;
        _visibleMenuItems = menu.Items
            .Where(item => item.Action is MainMenuAction.Preflight or MainMenuAction.OpenLogs)
            .ToArray();
        _applicationTitle = menu.Title;
        Title = "Vela";
        Width = Dim.Fill();
        Height = Dim.Fill();
        SchemeName = VelaTerminalTheme.Base;

        _header = new Label { X = 0, Y = 0, Width = Dim.Fill(), Text = BuildHeader(_applicationTitle, _dashboard, AutomaticPreflightState.Idle) };
        _modeBadge = new Label { X = Pos.AnchorEnd(14), Y = 0, Width = 14, Text = "[ TUI-MODE ]", SchemeName = VelaTerminalTheme.Muted };
        _navigationPanel = new FrameView { X = 0, Y = 2, Width = Dim.Percent(32), Height = 13, BorderStyle = Terminal.Gui.Drawing.LineStyle.None };
        _contentPanel = new FrameView { X = Pos.Right(_navigationPanel) + 2, Y = 2, Width = Dim.Fill(), Height = 13, BorderStyle = Terminal.Gui.Drawing.LineStyle.None };
        _navigationPanel.SchemeName = VelaTerminalTheme.Panel;
        _contentPanel.SchemeName = VelaTerminalTheme.Panel;
        _groupCaptions = new Label { X = 1, Y = 0, Width = Dim.Fill(1), Height = 2, Text = "功能导航" };
        _navigation = new ListView { X = 1, Y = 3, Width = Dim.Fill(1), Height = 6 };
        _navigation.SchemeName = VelaTerminalTheme.Navigation;
        _navigation.KeyDown += (_, key) =>
        {
            if (TryHandleQuitKey(key) ||
                TryHandleRunLifecycleKey(key) ||
                TryHandleActionPreviewKey(key) ||
                TryHandleRefreshKey(key))
            {
                key.Handled = true;
            }
        };
        _groupCaptions.SchemeName = VelaTerminalTheme.Muted;
        _navigationLabels = new ObservableCollection<string>(_visibleMenuItems.Select((item, index) => FormatNavigationLabel(item, index == 0)));
        _navigation.SetSource(_navigationLabels);
        _navigation.SelectedItem = 0;
        _navigation.ValueChanged += (_, _) =>
        {
            if (!_selectingLegacyMenuIndex)
            {
                _legacySelectedMenuIndex = null;
            }
            UpdateNavigationMarker();
            if (_navigationReady)
            {
                PreviewSelectedMenu();
            }
        };
        _navigation.Accepted += (_, _) => RequestVisibleAction(_navigation.SelectedItem ?? -1);
        _confirmationInput = new TextField
        {
            Visible = false,
            Width = 18
        };
        _confirmationInput.SchemeName = VelaTerminalTheme.Input;
        _confirmationInput.TextChanging += (_, args) =>
        {
            if (args.Result is { Length: > ConfirmationInputLimit } value)
            {
                args.Result = value[..ConfirmationInputLimit];
                args.Handled = true;
            }
        };
        _confirmationInput.Accepted += (_, _) => SubmitConfirmation(_confirmationInput.Text);
        _logList = new ListView { X = 1, Y = 2, Width = Dim.Fill(1), Height = Dim.Fill(), Visible = false, SchemeName = VelaTerminalTheme.Base };
        _logList.RowRender += (_, args) =>
        {
            if (args.Row < 0 || args.Row >= _logLines.Length) return;
            var line = _logLines[args.Row];
            var level = args.Row < _logLevels.Length ? _logLevels[args.Row] : RunEventLevel.Information;
            var scheme = level switch
            {
                RunEventLevel.Error => VelaTerminalTheme.Error,
                RunEventLevel.Warning => VelaTerminalTheme.Attention,
                _ when line.StartsWith("Enter ", StringComparison.Ordinal) => VelaTerminalTheme.Muted,
                _ => VelaTerminalTheme.Info
            };
            args.RowAttribute = VelaTerminalTheme.NormalAttribute(scheme);
        };
        _contentHeading = new Label { X = 1, Y = 0, Width = Dim.Fill(1), Height = 1, Text = "执行目标选择", SchemeName = VelaTerminalTheme.Info };
        _decision = new Label { X = 1, Y = 2, Width = Dim.Fill(1), Height = 1 };
        _workspace = new Label { X = 1, Y = 4, Width = Dim.Fill(1), Height = Dim.Fill(), SchemeName = VelaTerminalTheme.Base, Text = BuildOverview(_dashboard, AutomaticPreflightState.Idle) };
        _workspace.HotKeySpecifier = new System.Text.Rune(0xffff);
        _workspace.TextFormatter.HotKeySpecifier = new System.Text.Rune(0xffff);
        _homeView = new PreflightHomeView { X = 1, Y = 2, Width = Dim.Fill(1), Height = Dim.Fill(), Visible = true };
        _homeView.Apply(PreflightHomeViewModel.Create(
            Overview,
            _selectedTargetIndex,
            _targetLocked));
        _targetDetailView = new PreflightTargetDetailView
        {
            X = 1,
            Y = 2,
            Width = Dim.Fill(1),
            Height = Dim.Fill(),
            Visible = false
        };
        _targetDetailView.Apply(
            PreflightOverviewFormatter.CreateTargetDetail(
                Overview,
                PreflightHomeViewModel.Create(Overview, _selectedTargetIndex, _targetLocked)));
        _impactView = new CompactionImpactView
        {
            X = 1,
            Y = 2,
            Width = Dim.Fill(1),
            Height = Dim.Fill(),
            Visible = false
        };
        _evidencePanel = new FrameView { Title = "关键证据", Visible = false, SchemeName = VelaTerminalTheme.Panel };
        _evidence = new Label { X = 1, Y = 0, Width = Dim.Fill(1), Height = Dim.Fill(), SchemeName = VelaTerminalTheme.Base };
        _evidencePanel.Add(_evidence);
        _actionBar = new FrameView
        {
            X = 0,
            Y = Pos.AnchorEnd(1),
            Width = Dim.Fill(),
            Height = 1,
            BorderStyle = Terminal.Gui.Drawing.LineStyle.None,
            SchemeName = VelaTerminalTheme.ActionBar
        };
        _status = new Label { X = 1, Y = 0, Width = Dim.Fill(18), Text = "导航 / 操作  [↑↓] 导航   [Enter] 选择   [Esc] 退出" };
        _status.SchemeName = VelaTerminalTheme.ActionBar;
        _quitHint = new Label { X = Pos.AnchorEnd(15), Y = 0, Width = 14, Text = "[1-2] 切换模块" };
        _quitHint.SchemeName = VelaTerminalTheme.ActionBar;
        _header.SchemeName = VelaTerminalTheme.Info;
        _navigationPanel.Add(_groupCaptions, _navigation);
        _contentPanel.Add(_contentHeading, _decision, _workspace, _homeView, _targetDetailView, _impactView, _evidencePanel, _logList, _confirmationInput);
        _decision.Visible = false;
        _workspace.Visible = false;
        UpdateDecision(AutomaticPreflightState.Idle);
        _actionBar.Add(_status, _quitHint);
        Add(_header, _modeBadge, _navigationPanel, _contentPanel, _actionBar);
        AdaptTo(new Rectangle(0, 0, VelaLayoutMetrics.TwoPaneWidth, VelaLayoutMetrics.TwoPaneHeight));
        _navigationReady = true;
    }

    public VelaShellLayout LayoutMode { get; private set; }
    public VelaWorkspacePage CurrentPage { get; private set; } = VelaWorkspacePage.Overview;
    public AutomaticPreflightState PreflightState { get; private set; } = AutomaticPreflightState.Idle;
    public Guid CurrentProfileId { get; private set; }
    public string StatusText => _status.Text;
    public string QuitHintText => _quitHint.Text;
    public string ContentTitle => _contentHeading.Text;
    public string WorkspaceText => _workspace.Text;
    public bool HasLogAnalysis => _logAnalysis is not null;
    public long NavigationRevision => _navigationRevision;
    public int NavigationItemCount => _menuItems.Count;
    public int SelectedMenuIndex => _legacySelectedMenuIndex ?? _navigation.SelectedItem ?? 0;
    public int SelectedTargetIndex => _selectedTargetIndex;
    public WslDistribution? LockedTarget => _targetLocked && _lockedTargetName is { Length: > 0 }
        ? Overview.InstalledDistros.FirstOrDefault(distribution =>
            string.Equals(distribution.Name, _lockedTargetName, StringComparison.OrdinalIgnoreCase))
        : null;
    public string? LockedTargetName => LockedTarget?.Name;
    public string? LockedTargetVhdxPath
    {
        get
        {
            var target = LockedTarget;
            if (!string.IsNullOrWhiteSpace(target?.VhdxPath))
            {
                return target.VhdxPath;
            }

            return target is not null &&
                string.Equals(target.Name, _dashboard.DistroName, StringComparison.OrdinalIgnoreCase)
                ? _dashboard.VhdxEvidence?.FilePath ?? _dashboard.ConfiguredVhdxPath
                : null;
        }
    }
    public long? LockedTargetVhdxSizeBytes
    {
        get
        {
            var target = LockedTarget;
            if (target?.VhdxSizeBytes is { } sizeBytes)
            {
                return sizeBytes;
            }

            return target is not null &&
                string.Equals(target.Name, _dashboard.DistroName, StringComparison.OrdinalIgnoreCase) &&
                _dashboard.VhdxEvidence is { } evidence
                ? evidence.FileLengthBytes
                : null;
        }
    }
    public MainMenuAction SelectedAction => _legacySelectedMenuIndex is { } legacyIndex
        ? _menuItems[legacyIndex].Action
        : _visibleMenuItems[SelectedMenuIndex].Action;
    public bool HasSingleNavigationFocus => true;
    public string DecisionSchemeName => _decision.SchemeName ?? VelaTerminalTheme.Muted;
    public PreflightOverviewViewModel Overview =>
        PreflightOverviewViewModel.Create(_dashboard, PreflightState);
    public event Action<MainMenuAction>? ActionRequested;
    public event Action<MainMenuAction, long>? SelectionPreviewRequested;
    public event Action<ConfirmationInputResult>? ConfirmationSubmitted;

    public Profile? CreateLockedTargetProfile(Profile baseProfile) =>
        CompactionTargetProfileFactory.Create(baseProfile, LockedTarget);

    public OperationRequest? CreateLockedCompactionRequest(Profile baseProfile, Guid runId) =>
        CompactionTargetProfileFactory.CreateRequest(runId, baseProfile, LockedTarget);

    private bool CanExecuteLockedTarget
    {
        get
        {
            if (LockedTarget is null ||
                PreflightState.ProfileId != CurrentProfileId ||
                PreflightState.Status is AutomaticPreflightStatus.Idle or AutomaticPreflightStatus.Checking or AutomaticPreflightStatus.Failed or AutomaticPreflightStatus.Stale)
            {
                return false;
            }

            var home = PreflightHomeViewModel.Create(
                Overview,
                _selectedTargetIndex,
                targetLocked: true);
            return PreflightOverviewFormatter
                .CreateTargetDetail(Overview, home)
                .IsReady;
        }
    }

    public bool IsCurrentSelection(MainMenuAction action, long revision) =>
        revision == _navigationRevision && SelectedAction == action;

    public void SelectMenuIndex(int selectedIndex)
    {
        if (selectedIndex >= 0 && selectedIndex < _menuItems.Count)
        {
            _legacySelectedMenuIndex = selectedIndex;
            _lastPreviewedSelection = -1;
            if (selectedIndex < _visibleMenuItems.Count)
            {
                _selectingLegacyMenuIndex = true;
                try
                {
                    _navigation.SelectedItem = selectedIndex;
                }
                finally
                {
                    _selectingLegacyMenuIndex = false;
                }
            }
            PreviewSelectedMenu();
            SetNeedsDraw();
        }
    }

    public void ShowStatus(string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        // Keep the operation affordance visible even when a transient status message is shown.
        // The action hint leads the row so it remains visible on compact terminals.
        var safeMessage = TuiDisplayText.Sanitize(message, 96);
        _status.Text = $"{BuildActionHint()}   ·   {safeMessage}";
        SetNeedsDraw();
    }

    public void ShowWorkspacePage(string title, IEnumerable<string> lines)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentNullException.ThrowIfNull(lines);
        CurrentPage = title switch
        {
            "目标档案" => VelaWorkspacePage.Profiles,
            "最近运行" => VelaWorkspacePage.RecentRuns,
            "运行日志" => VelaWorkspacePage.Logs,
            _ => VelaWorkspacePage.ActionPreview
        };
        _pageTitle = TuiDisplayText.Sanitize(title, 32);
        _pageLines = lines.Take(20).Select(line => TuiDisplayText.Sanitize(line, 96)).ToArray();
        SetContentTitle(_pageTitle);
        _evidencePanel.Visible = false;
        _logList.Visible = false;
        _homeView.Visible = false;
        _targetDetailView.Visible = false;
        _impactView.Visible = false;
        _contentHeading.Visible = true;
        _workspace.Visible = true;
        SetOverviewDecisionVisible(false);
        _workspace.Text = BuildPage();
        SetNavigationStatus();
        _homeView.SetFocus();
        SetNeedsDraw();
    }

    public void ShowLogPage(IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ShowLogPage(lines.Select(line => new RunLogLine(line, RunEventLevel.Information)));
    }

    public void ShowLogPage(IEnumerable<RunLogLine> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        CurrentPage = VelaWorkspacePage.Logs;
        SetContentTitle("运行日志");
        SetOverviewDecisionVisible(false);
        _homeView.Visible = false;
        _targetDetailView.Visible = false;
        _impactView.Visible = false;
        _contentHeading.Visible = true;
        _workspace.Visible = false;
        UpdateLogViewLayout();
        _logEntries = lines.Take(20).ToArray();
        RefreshLogLines();
        SetNavigationStatus();
        _navigation.SetFocus();
        SetNeedsDraw();
    }

    public void ShowLogAnalysis(RunLogSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        CurrentPage = VelaWorkspacePage.LogAnalysis;
        _logSnapshot = snapshot;
        _logAnalysis = RunLogAnalyzer.Analyze(snapshot);
        _logEntries = BuildLogEntries(snapshot);
        SetContentTitle("日志分析");
        SetOverviewDecisionVisible(false);
        _homeView.Visible = false;
        _targetDetailView.Visible = false;
        _impactView.Visible = false;
        _contentHeading.Visible = true;
        UpdateEvidence();
        _workspace.Visible = true;
        UpdateLogViewLayout();
        _workspace.Text = BuildLogAnalysis();
        RefreshLogLines();
        SetNavigationStatus();
        _navigation.SetFocus();
        SetNeedsDraw();
    }

    public void ShowOverview()
    {
        CurrentPage = VelaWorkspacePage.Overview;
        SetContentTitle("执行目标选择");
        _header.Text = BuildHeader(_applicationTitle, _dashboard, PreflightState);
        UpdateEvidence();
        _logList.Visible = false;
        _targetDetailView.Visible = false;
        _impactView.Visible = false;
        _contentHeading.Visible = true;
        SetOverviewDecisionVisible(false);
        UpdateDecision(PreflightState);
        _workspace.Text = BuildOverview(_dashboard, PreflightState);
        ApplyOverviewSurface();
        _homeView.SetFocus();
        SetNavigationStatus();
        SetNeedsDraw();
    }

    public void ShowActionPreview(string title, IEnumerable<string> lines)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentNullException.ThrowIfNull(lines);
        CurrentPage = VelaWorkspacePage.ActionPreview;
        _evidencePanel.Visible = false;
        _logList.Visible = false;
        _homeView.Visible = false;
        _targetDetailView.Visible = false;
        _impactView.Visible = false;
        _workspace.Visible = true;
        _contentHeading.Visible = true;
        SetOverviewDecisionVisible(false);
        SetContentTitle(TuiDisplayText.Sanitize(title, 32));
        _workspace.Text = string.Join(Environment.NewLine, lines.Take(_availablePageRows).Select(line => TuiDisplayText.Sanitize(line, 96)));
        SetNavigationStatus();
        _navigation.SetFocus();
        SetNeedsDraw();
    }

    private void ShowCompactionImpactPreview()
    {
        CurrentPage = VelaWorkspacePage.ActionPreview;
        _evidencePanel.Visible = false;
        _logList.Visible = false;
        _homeView.Visible = false;
        _targetDetailView.Visible = false;
        _workspace.Visible = false;
        _impactView.Visible = true;
        _contentHeading.Visible = false;
        SetOverviewDecisionVisible(false);
        // Keep the legacy content title for callers that use the shell as a
        // state projection; the visible assessment heading is rendered by the
        // dedicated impact view below.
        SetContentTitle("影响预览 · 影响评估（Impact Assessment）");
        _header.Text = BuildHeader(_applicationTitle, _dashboard, PreflightState);
        _workspace.Text = string.Join(
            Environment.NewLine,
            BuildCompactionPreview()
                .Take(_availablePageRows)
                .Select(line => TuiDisplayText.Sanitize(line, 96)));
        UpdateCompactionImpactView();
        SetNavigationStatus();
        _navigation.SetFocus();
        SetNeedsDraw();
    }

    public bool ApplyCompactionImpactEstimate(
        long revision,
        string distroName,
        CompactionImpactEstimate estimate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(distroName);
        ArgumentNullException.ThrowIfNull(estimate);

        if (!IsCurrentSelection(MainMenuAction.ExecuteCompaction, revision) ||
            CurrentPage != VelaWorkspacePage.ActionPreview ||
            !string.Equals(LockedTargetName, distroName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        _compactionEstimate = estimate;
        _workspace.Text = string.Join(
            Environment.NewLine,
            BuildCompactionPreview()
                .Take(_availablePageRows)
                .Select(line => TuiDisplayText.Sanitize(line, 96)));
        UpdateCompactionImpactView();
        SetNeedsDraw();
        return true;
    }

    public void ShowRunProgress(RunProgressViewModel progress)
    {
        ArgumentNullException.ThrowIfNull(progress);
        CurrentPage = progress.State is RunProgressState.Succeeded or RunProgressState.Failed
            or RunProgressState.Cancelled or RunProgressState.TimedOut or RunProgressState.ReadFailed
            ? VelaWorkspacePage.Result
            : VelaWorkspacePage.Running;
        _evidencePanel.Visible = false;
        _logList.Visible = false;
        _homeView.Visible = false;
        _targetDetailView.Visible = false;
        _impactView.Visible = false;
        _contentHeading.Visible = true;
        _workspace.Visible = true;
        SetOverviewDecisionVisible(false);
        SetContentTitle(CurrentPage == VelaWorkspacePage.Result ? "运行结果" : "运行进度");
        _workspace.Text = BuildRunProgress(progress);
        SetNavigationStatus();
        if (CurrentPage == VelaWorkspacePage.Running)
        {
            _workspace.SetFocus();
        }
        else
        {
            _navigation.SetFocus();
        }
        SetNeedsDraw();
    }

    public void ShowConfirmation(ConfirmationViewModel confirmation)
    {
        ArgumentNullException.ThrowIfNull(confirmation);
        _confirmation = confirmation;
        CurrentPage = VelaWorkspacePage.Confirmation;
        _evidencePanel.Visible = false;
        _logList.Visible = false;
        _homeView.Visible = false;
        _targetDetailView.Visible = false;
        _impactView.Visible = false;
        _contentHeading.Visible = true;
        _workspace.Visible = true;
        SetOverviewDecisionVisible(true);
        _decision.Text = "! 影响摘要：请核对停止范围、运行中的发行版和 VHDX 状态";
        _decision.SchemeName = VelaTerminalTheme.Attention;
        SetContentTitle("执行确认");
        _confirmationInput.Text = string.Empty;
        _confirmationInput.Visible = true;
        _workspace.Height = Dim.Fill(2);
        _workspace.Text = BuildConfirmation(confirmation);
        SetNavigationStatus();
        _confirmationInput.SetFocus();
        SetNeedsDraw();
    }

    public void SubmitConfirmation(string? response)
    {
        if (_confirmation is null) return;
        var boundedResponse = response is null || response.Length <= ConfirmationInputLimit
            ? response
            : response[..ConfirmationInputLimit];
        var result = ExactConfirmationPolicy.IsAccepted(boundedResponse)
            ? new ConfirmationInputResult(ConfirmationInputStatus.Accepted, boundedResponse!)
            : new ConfirmationInputResult(ConfirmationInputStatus.Rejected, boundedResponse ?? string.Empty);
        if (result.Status == ConfirmationInputStatus.Rejected)
        {
            ShowStatus("确认输入未匹配 YES，操作未启动。");
        }
        else
        {
            _confirmation = null;
            _confirmationInput.Visible = false;
            _workspace.Height = Dim.Fill();
            CurrentPage = VelaWorkspacePage.Result;
            SetOverviewDecisionVisible(false);
            SetContentTitle("确认结果");
            _workspace.Text = "确认已记录；当前只读会话未启动操作。";
            ShowStatus("确认已记录，操作未启动。");
        }
        ConfirmationSubmitted?.Invoke(result);
    }

    public void CancelConfirmation()
    {
        if (_confirmation is null) return;
        _confirmation = null;
        _confirmationInput.Visible = false;
        _workspace.Height = Dim.Fill();
        ResetNavigationToOverview();
        _navigation.SetFocus();
        CurrentPage = VelaWorkspacePage.Overview;
        UpdateEvidence();
        _logList.Visible = false;
        _workspace.Visible = false;
        _homeView.Visible = true;
        _targetDetailView.Visible = false;
        _impactView.Visible = false;
        _contentHeading.Visible = true;
        SetOverviewDecisionVisible(true);
        UpdateDecision(PreflightState);
        SetContentTitle("执行目标选择");
        _workspace.Text = BuildOverview(_dashboard, PreflightState);
        _homeView.Apply(PreflightHomeViewModel.Create(
            Overview,
            _selectedTargetIndex,
            _targetLocked));
        _homeView.SetFocus();
        ShowStatus("确认已取消，操作未启动。");
        ConfirmationSubmitted?.Invoke(new ConfirmationInputResult(ConfirmationInputStatus.Cancelled, string.Empty));
    }

    public void SetCurrentProfile(Vela.Core.Models.Profile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        CurrentProfileId = profile.Id;
        _dashboard = DashboardViewModel.CreateInitial(profile);
        PreflightState = AutomaticPreflightState.Idle;
        CurrentPage = VelaWorkspacePage.Overview;
        ResetNavigationToOverview();
        _selectedTargetIndex = 0;
        _targetLocked = false;
        _lockedTargetName = null;
        _compactionEstimate = null;
        UpdateEvidence();
        _logList.Visible = false;
        _targetDetailView.Visible = false;
        SetOverviewDecisionVisible(false);
        UpdateDecision(PreflightState);
        SetContentTitle("执行目标选择");
        _header.Text = BuildHeader(_applicationTitle, _dashboard, PreflightState);
        _workspace.Text = BuildOverview(_dashboard, PreflightState);
        ApplyOverviewSurface();
        _homeView.SetFocus();
        SetNavigationStatus();
        SetNeedsDraw();
    }

    public void RequestAction(int selectedIndex)
    {
        if (selectedIndex < 0 || selectedIndex >= _menuItems.Count) return;
        RequestAction(_menuItems[selectedIndex].Action);
    }

    private void RequestVisibleAction(int selectedIndex)
    {
        if (selectedIndex < 0 || selectedIndex >= _visibleMenuItems.Count)
        {
            return;
        }

        _legacySelectedMenuIndex = null;
        RequestAction(_visibleMenuItems[selectedIndex].Action);
    }

    public void RequestPreflightRefresh()
    {
        if ((CurrentPage is VelaWorkspacePage.Overview or VelaWorkspacePage.TargetDetail) &&
            SelectedAction == MainMenuAction.Preflight)
        {
            _selectedTargetIndex = 0;
            _targetLocked = false;
            _lockedTargetName = null;
            _compactionEstimate = null;
            CurrentPage = VelaWorkspacePage.Overview;
            SetContentTitle("执行目标选择");
            ApplyOverviewSurface();
            _header.Text = BuildHeader(_applicationTitle, _dashboard, PreflightState);
            _homeView.SetFocus();
            SetNavigationStatus();
            RequestAction(MainMenuAction.Preflight);
        }
    }

    public void AdaptTo(Rectangle screen)
    {
        _screenWidth = screen.Width;
        _screenHeight = screen.Height;
        var metrics = VelaLayoutMetrics.Calculate(screen.Width, screen.Height);
        Height = Dim.Fill();
        LayoutMode = metrics.Layout;
        _header.Text = BuildHeader(_applicationTitle, _dashboard, PreflightState);
        _header.Y = 0;
        _modeBadge.Visible = screen.Width >= 100;
        _header.Width = _modeBadge.Visible ? Dim.Fill(14) : Dim.Fill();
        _actionBar.Y = Pos.AnchorEnd(1);
        _actionBar.Width = Dim.Fill();
        _status.X = 1;
        _quitHint.Visible = screen.Width >= 44;
        _status.Width = _quitHint.Visible ? Dim.Fill(16) : Dim.Fill(2);
        _quitHint.X = Pos.AnchorEnd(15);
        if (LayoutMode == VelaShellLayout.TwoPane)
        {
            var showsEvidenceRail = ShouldShowEvidenceRail;
            _groupCaptions.Visible = true;
            _navigationPanel.X = 0; _navigationPanel.Y = 2;
            _navigationPanel.Width = showsEvidenceRail ? Dim.Percent(26) : 28;
            _navigationPanel.Height = Dim.Fill(1);
            _contentPanel.X = Pos.Right(_navigationPanel) + 1; _contentPanel.Y = 2; _contentPanel.Width = Dim.Fill(); _contentPanel.Height = Dim.Fill(1);
            _groupCaptions.X = 1; _groupCaptions.Y = 0; _groupCaptions.Width = Dim.Fill(1);
            _navigation.X = 1; _navigation.Y = 3; _navigation.Width = Dim.Fill(1); _navigation.Height = 3;
            _workspace.X = 1; _workspace.Y = _decision.Visible ? 4 : 2;
            _workspace.Width = showsEvidenceRail ? Dim.Percent(58) : Dim.Fill(1);
            _workspace.Height = _confirmationInput.Visible ? Dim.Fill(2) : Dim.Fill();
            _homeView.X = 1; _homeView.Y = _decision.Visible ? 4 : 2;
            _homeView.Width = Dim.Fill(1);
            _homeView.Height = _confirmationInput.Visible ? Dim.Fill(2) : Dim.Fill();
            _targetDetailView.X = 1; _targetDetailView.Y = 2;
            _targetDetailView.Width = Dim.Fill(1);
            _targetDetailView.Height = Dim.Fill();
            _impactView.X = 1; _impactView.Y = 2; _impactView.Width = Dim.Fill(1); _impactView.Height = Dim.Fill();
            _decision.Width = showsEvidenceRail ? Dim.Percent(58) : Dim.Fill(1);
            _evidencePanel.X = Pos.Right(_workspace) + 1; _evidencePanel.Y = 2; _evidencePanel.Width = Dim.Fill(); _evidencePanel.Height = 11;
            _logList.X = 1; _logList.Width = showsEvidenceRail && CurrentPage == VelaWorkspacePage.LogAnalysis ? Dim.Percent(58) : Dim.Fill(1); _logList.Height = Dim.Fill();
            _confirmationInput.X = 1; _confirmationInput.Y = Pos.AnchorEnd(1);
            _availablePageRows = metrics.AvailablePageRows;
        }
        else
        {
            _groupCaptions.Visible = false;
            var navigationHeight = metrics.NavigationHeight;
            _navigationPanel.X = 0; _navigationPanel.Y = 1; _navigationPanel.Width = Dim.Fill(); _navigationPanel.Height = navigationHeight;
            _contentPanel.X = 0; _contentPanel.Y = Pos.Bottom(_navigationPanel); _contentPanel.Width = Dim.Fill(); _contentPanel.Height = Dim.Fill(1);
            _navigation.X = 1; _navigation.Y = 0; _navigation.Width = Dim.Fill(1); _navigation.Height = 3;
            _workspace.X = 1; _workspace.Y = _decision.Visible ? 3 : 2; _workspace.Width = Dim.Fill(1); _workspace.Height = _confirmationInput.Visible ? Dim.Fill(2) : Dim.Fill();
            _homeView.X = 1; _homeView.Y = _decision.Visible ? 3 : 2; _homeView.Width = Dim.Fill(1); _homeView.Height = _confirmationInput.Visible ? Dim.Fill(2) : Dim.Fill();
            _targetDetailView.X = 1; _targetDetailView.Y = 2; _targetDetailView.Width = Dim.Fill(1); _targetDetailView.Height = Dim.Fill();
            _impactView.X = 1; _impactView.Y = 2; _impactView.Width = Dim.Fill(1); _impactView.Height = Dim.Fill();
            _decision.Width = Dim.Fill(1);
            _evidencePanel.Visible = false;
            _logList.X = 1; _logList.Width = Dim.Fill(1); _logList.Height = Dim.Fill();
            _confirmationInput.X = 1; _confirmationInput.Y = Pos.AnchorEnd(1);
            _availablePageRows = metrics.AvailablePageRows;
        }

        // Re-evaluate the hint after every resize so compact terminals never retain
        // a clipped wide-screen command string.
        SetNavigationStatus();
        ApplyContentRailLayout();
        UpdateLogViewLayout();

        if (CurrentPage is VelaWorkspacePage.Profiles or VelaWorkspacePage.RecentRuns)
        {
            _workspace.Text = BuildPage();
        }
        else if (CurrentPage == VelaWorkspacePage.Logs)
        {
            RefreshLogLines();
        }
        else if (CurrentPage == VelaWorkspacePage.LogAnalysis)
        {
            _workspace.Text = BuildLogAnalysis();
        }
        else if (CurrentPage == VelaWorkspacePage.Overview)
        {
            _workspace.Text = BuildOverview(_dashboard, PreflightState);
            ApplyOverviewSurface();
            UpdateEvidence();
        }
        else if (CurrentPage == VelaWorkspacePage.TargetDetail)
        {
            ApplyTargetDetailSurface();
        }
        else if (CurrentPage == VelaWorkspacePage.ActionPreview && SelectedAction == MainMenuAction.ExecuteCompaction)
        {
            _workspace.Text = string.Join(
                Environment.NewLine,
                BuildCompactionPreview()
                    .Take(_availablePageRows)
                    .Select(line => TuiDisplayText.Sanitize(line, 96)));
            UpdateCompactionImpactView();
        }
    }

    public void ApplyPreflight(AutomaticPreflightState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        PreflightState = state;
        if (state.Dashboard is not null)
        {
            _dashboard = state.Dashboard;
            _compactionEstimate = null;
            SyncLockedTarget();
        }
        _header.Text = BuildHeader(_applicationTitle, _dashboard, state);
        UpdateDecision(state);
        if (CurrentPage == VelaWorkspacePage.Overview)
        {
            _workspace.Text = BuildOverview(_dashboard, state);
            ApplyOverviewSurface();
        }
        else if (CurrentPage == VelaWorkspacePage.TargetDetail)
        {
            ApplyTargetDetailSurface();
        }
        UpdateEvidence();
        SetNavigationStatus();
        SetNeedsDraw();
    }

    internal bool TryHandleTargetNavigationKey(Key key)
    {
        if (CurrentPage != VelaWorkspacePage.Overview ||
            SelectedAction != MainMenuAction.Preflight ||
            !_homeView.HasFocus)
        {
            return false;
        }

        var home = PreflightHomeViewModel.Create(Overview, _selectedTargetIndex, _targetLocked);
        if (home.Targets.Length == 0)
        {
            return false;
        }

        if (key == Key.CursorUp)
        {
            SelectTarget(-1, home.Targets.Length);
            return true;
        }

        if (key == Key.CursorDown)
        {
            SelectTarget(1, home.Targets.Length);
            return true;
        }

        if (key == Key.Enter)
        {
            OpenSelectedTargetDetail(home);
            return true;
        }

        return false;
    }

    internal bool TryHandleFocusToggleKey(Key key)
    {
        if (CurrentPage != VelaWorkspacePage.Overview || key != Key.Tab)
        {
            return false;
        }

        if (_homeView.HasFocus)
        {
            _navigation.SetFocus();
        }
        else
        {
            _homeView.SetFocus();
        }

        SetNavigationStatus();
        SetNeedsDraw();
        return true;
    }

    internal bool TryHandleTargetDetailKey(Key key)
    {
        if (CurrentPage != VelaWorkspacePage.TargetDetail || !_targetDetailView.HasFocus)
        {
            return false;
        }

        if (key == Key.Esc)
        {
            _targetLocked = false;
            _lockedTargetName = null;
            _compactionEstimate = null;
            ResetNavigationToOverview();
            ShowOverview();
            return true;
        }

        if (key == Key.Enter)
        {
            if (!CanExecuteLockedTarget)
            {
                ShowStatus("当前锁定目标的预检尚未通过，先处理检查项或按 R 重扫");
            }
            else
            {
                OpenCompactionImpactFromDetail();
            }
            return true;
        }

        return false;
    }

    internal bool TryHandleActionPreviewKey(Key key)
    {
        if (CurrentPage != VelaWorkspacePage.ActionPreview ||
            SelectedAction != MainMenuAction.ExecuteCompaction)
        {
            return false;
        }

        if (key == Key.Esc)
        {
            _compactionEstimate = null;
            ResetNavigationToOverview();
            ApplyTargetDetailSurface();
            return true;
        }

        if (!IsYesKey(key))
        {
            return false;
        }

        RequestAction(MainMenuAction.ExecuteCompaction);
        return true;
    }

    internal bool TryHandleQuitKey(Key key)
    {
        if (!IsQuitKey(key))
        {
            return false;
        }

        RequestAction(MainMenuAction.Exit);
        return true;
    }

    internal bool TryHandleRunLifecycleKey(Key key)
    {
        if (CurrentPage == VelaWorkspacePage.Running)
        {
            // A running view is driven by the journal callback. Navigation keys
            // must not move the menu while the worker owns the operation.
            return true;
        }

        if (CurrentPage == VelaWorkspacePage.Result &&
            (key == Key.Enter || key == Key.Esc))
        {
            ResetNavigationToOverview();
            ShowOverview();
            return true;
        }

        return false;
    }

    private void SelectTarget(int direction, int targetCount)
    {
        if (targetCount <= 0)
        {
            return;
        }

        _selectedTargetIndex = Math.Clamp(
            _selectedTargetIndex + direction,
            0,
            targetCount - 1);
        _targetLocked = false;
        _lockedTargetName = null;
        _compactionEstimate = null;
        ApplyOverviewSurface();
        _header.Text = BuildHeader(_applicationTitle, _dashboard, PreflightState);
        SetNavigationStatus();
        SetNeedsDraw();
    }

    private void OpenSelectedTargetDetail(PreflightHomeViewModel home)
    {
        _selectedTargetIndex = Math.Clamp(_selectedTargetIndex, 0, home.Targets.Length - 1);
        _lockedTargetName = home.Targets[_selectedTargetIndex].DistroName;
        _targetLocked = true;
        _compactionEstimate = null;
        _header.Text = BuildHeader(_applicationTitle, _dashboard, PreflightState);
        ApplyTargetDetailSurface();
    }

    private void OpenCompactionImpactFromDetail()
    {
        // 02 is a workflow step in the design, not a second sidebar focus row.
        // Keep the workspace row active while retaining the legacy action index
        // for the existing program event contract.
        _legacySelectedMenuIndex = 1;
        _lastPreviewedSelection = -1;
        PreviewSelectedMenu();
        SetNeedsDraw();
    }

    protected override bool OnKeyDown(Key key)
    {
        // Terminal.Gui represents lowercase r as Key.R and uppercase R as
        // Key.R.WithShift. Normalize both forms so the visible [R] hint is
        // reliable in tmux and on a physical keyboard.
        if (TryHandleRunLifecycleKey(key)) return true;
        if (TryHandleQuitKey(key)) return true;
        if (TryHandleFocusToggleKey(key)) return true;
        if (TryHandleTargetNavigationKey(key)) return true;
        if (TryHandleTargetDetailKey(key)) return true;
        if (TryHandleActionPreviewKey(key)) return true;
        if (TryHandleRefreshKey(key)) return true;
        if (key == Key.Esc && _confirmation is not null)
        {
            CancelConfirmation();
            return true;
        }
        if (key == Key.Esc && CurrentPage != VelaWorkspacePage.Overview)
        {
            ResetNavigationToOverview();
            ShowOverview();
            return true;
        }
        return base.OnKeyDown(key);
    }

    internal bool TryHandleRefreshKey(Key key)
    {
        if (!IsRefreshKey(key) ||
            (CurrentPage is not (VelaWorkspacePage.Overview or VelaWorkspacePage.TargetDetail)) ||
            SelectedAction != MainMenuAction.Preflight)
        {
            return false;
        }

        RequestPreflightRefresh();
        return true;
    }

    private static bool IsRefreshKey(Key key) =>
        !key.IsCtrl && !key.IsAlt && key.NoShift == Key.R;

    private static bool IsYesKey(Key key) =>
        !key.IsCtrl && !key.IsAlt && key.NoShift == Key.Y;

    private static bool IsQuitKey(Key key) =>
        !key.IsCtrl && !key.IsAlt && key.NoShift == Key.Q;

    private void ResetNavigationToOverview()
    {
        // Esc returns to the same page represented by the focused item. This
        // avoids a stale 04/05 marker sitting beside an overview preview.
        _legacySelectedMenuIndex = null;
        _navigationRevision++;
        _lastPreviewedSelection = 0;
        _navigation.SelectedItem = 0;
        UpdateNavigationMarker();
    }

    private void RequestAction(MainMenuAction action)
    {
        if (action == MainMenuAction.ExecuteCompaction &&
            LockedTarget is null &&
            !PreflightState.CanExecuteCompaction)
        {
            ShowStatus("执行压缩前需要完成当前档案的只读预检");
            return;
        }

        if (action == MainMenuAction.ExecuteCompaction &&
            PreflightState.ProfileId != CurrentProfileId)
        {
            ShowStatus("执行压缩前需要完成当前档案的只读预检");
            return;
        }

        if (action == MainMenuAction.ExecuteCompaction && LockedTarget is null)
        {
            ShowStatus(PreflightState.CanExecuteCompaction
                ? "请先在 01 预检结果中锁定一个实例"
                : "执行压缩前需要完成当前档案的只读预检");
            return;
        }

        if (action == MainMenuAction.ExecuteCompaction && !CanExecuteLockedTarget)
        {
            ShowStatus("当前锁定目标的只读预检未通过，暂不可进入执行确认");
            return;
        }

        ActionRequested?.Invoke(action);
    }

    /// <summary>
    /// Navigation is a live preview: moving the single focus list changes the
    /// read-only content surface immediately. Enter still remains the commit
    /// key for the selected action.
    /// </summary>
    private void PreviewSelectedMenu()
    {
        if (!_navigationReady || _navigation.SelectedItem is not { } selected
            || selected < 0 || selected >= _visibleMenuItems.Count
            || _lastPreviewedSelection == selected)
        {
            return;
        }

        _lastPreviewedSelection = selected;
        _navigationRevision++;
        var action = SelectedAction;
        switch (action)
        {
            case MainMenuAction.Preflight:
                ShowOverview();
                break;
            case MainMenuAction.ExecuteCompaction:
                _compactionEstimate = null;
                ShowCompactionImpactPreview();
                break;
            case MainMenuAction.ManageProfiles:
                ShowWorkspacePage("目标档案", BuildProfilePreview());
                break;
            case MainMenuAction.RecentRuns:
                ShowWorkspacePage("最近运行", [
                    "最近运行记录将在当前界面读取。",
                    "按 Enter 读取最新记录；Esc 返回状态总览。"
                ]);
                break;
            case MainMenuAction.OpenLogs:
                ShowLogSelectionPreview();
                break;
            case MainMenuAction.Exit:
                ShowActionPreview("退出 Vela", [
                    "当前会话保持只读。",
                    "按 Enter 退出 Vela；Esc 返回状态总览。"
                ]);
                break;
        }

        SelectionPreviewRequested?.Invoke(action, _navigationRevision);
    }

    private string[] BuildCompactionPreview()
    {
        var target = LockedTarget;
        if (target is null)
        {
            return
            [
                "STEP2_PREVIEW  压缩影响预览",
                "目标       尚未锁定",
                "状态       返回 01 预检结果选择实例",
                "",
                "当前体积   未读取",
                "预计可回收空间  等待锁定目标"
            ];
        }

        var overview = Overview;
        var targetPath = string.IsNullOrWhiteSpace(target.VhdxPath) &&
            string.Equals(target.Name, overview.DistroName, StringComparison.OrdinalIgnoreCase)
                ? overview.Evidence.FilePath
                : target.VhdxPath;
        var targetSize = target.VhdxSizeBytes is { } sizeBytes
            ? PreflightOverviewFormatter.FormatCapacity(sizeBytes)
            : string.Equals(target.Name, overview.DistroName, StringComparison.OrdinalIgnoreCase)
                ? overview.Evidence.FileSize
                : "尚未采集";
        var formattedPath = PreflightOverviewFormatter.FormatVhdxPath(targetPath, 96);

        return
        [
            "STEP2_PREVIEW  压缩影响预览",
            $"目标       {TuiDisplayText.Sanitize(target.Name, 64)}",
            $"当前体积   {targetSize}",
            "预计体积   执行后读取",
            $"预计可回收空间  {FormatCompactionEstimate()}",
            "估算口径   当前 VHDX 体积 − 根文件系统已用空间",
            $"VHDX       {(string.IsNullOrWhiteSpace(formattedPath) ? "未读取" : formattedPath)}",
            "",
            "[Y] 开始执行 · [Enter] 进入 YES 确认",
            "当前页面只读取锁定目标，不切换发行版。"
        ];
    }

    private string FormatCompactionEstimate() => _compactionEstimate?.Status switch
    {
        CompactionImpactStatus.Estimated when _compactionEstimate.ReclaimableBytes is { } bytes =>
            PreflightOverviewFormatter.FormatCapacity(bytes),
        CompactionImpactStatus.Failed => "采集失败",
        CompactionImpactStatus.Unavailable => "暂不可用",
        _ => "计算中…"
    };

    private void UpdateCompactionImpactView()
    {
        var target = LockedTarget;
        var targetSizeBytes = LockedTargetVhdxSizeBytes;
        var estimateSizeBytes = _compactionEstimate?.CurrentVhdxSizeBytes;
        var currentBytes = estimateSizeBytes ?? targetSizeBytes;
        var reclaimableBytes = _compactionEstimate?.Status == CompactionImpactStatus.Estimated
            ? _compactionEstimate.ReclaimableBytes
            : null;
        var projectedBytes = currentBytes is { } current && reclaimableBytes is { } reclaim
            ? Math.Max(0, current - reclaim)
            : (long?)null;
        var currentSize = currentBytes is { } currentValue
            ? PreflightOverviewFormatter.FormatCapacity(currentValue)
            : target is null
                ? "尚未锁定"
                : "尚未读取";
        var projectedSize = projectedBytes is { } projected
            ? PreflightOverviewFormatter.FormatCapacity(projected)
            : "计算中…";
        var reclaimableSize = reclaimableBytes is { } reclaimValue
            ? PreflightOverviewFormatter.FormatCapacity(reclaimValue)
            : FormatCompactionEstimate();
        _impactView.Apply(
            target?.Name ?? string.Empty,
            currentSize,
            projectedSize,
            reclaimableSize,
            _compactionEstimate?.Status == CompactionImpactStatus.Estimated && projectedBytes is not null);
    }

    private string BuildRunProgress(RunProgressViewModel progress)
    {
        var target = string.IsNullOrWhiteSpace(progress.TargetName)
            ? "当前锁定目标"
            : TuiDisplayText.Sanitize(progress.TargetName, 64);
        var path = PreflightOverviewFormatter.FormatVhdxPath(progress.VhdxPath, 96);
        var visibleLogs = progress.VisibleLogLines
            .TakeLast(Math.Max(3, _availablePageRows - 8))
            .Select(line => TuiDisplayText.Sanitize(line, 160))
            .ToArray();

        if (progress.State == RunProgressState.Running)
        {
            var lines = new List<string>
            {
                "STEP2_RUNNING  ▪",
                $"目标       {target}",
                $"VHDX       {(string.IsNullOrWhiteSpace(path) ? "未读取" : path)}",
                $"进度       {FormatProgressBar(progress.Percent)}",
                $"状态       执行中 · {TuiDisplayText.Sanitize(progress.Message, 120)}",
                "",
                "Console Log"
            };
            lines.AddRange(visibleLogs.Length == 0
                ? ["[INFO] 等待 worker journal 事件。"]
                : visibleLogs);
            return string.Join(Environment.NewLine, lines);
        }

        var stateLabel = progress.State switch
        {
            RunProgressState.Succeeded => "✔ DONE",
            RunProgressState.Cancelled => "! CANCELLED",
            RunProgressState.TimedOut => "! TIMEOUT",
            RunProgressState.ReadFailed => "× JOURNAL READ FAILED",
            _ => "× FAILED"
        };
        var elapsed = progress.Elapsed is { } duration
            ? duration.ToString(duration.TotalHours >= 1 ? @"hh\:mm\:ss" : @"mm\:ss", System.Globalization.CultureInfo.InvariantCulture)
            : "未知";
        var reclaimed = progress.ReclaimedBytes is { } bytes
            ? PreflightOverviewFormatter.FormatCapacity(bytes)
            : "未知";
        return string.Join(Environment.NewLine,
            stateLabel,
            "",
            $"目标       {target}",
            $"耗时       {elapsed}",
            $"实际释放   {reclaimed}",
            $"VHDX       {(string.IsNullOrWhiteSpace(path) ? "未读取" : path)}",
            "",
            $"终态       {TuiDisplayText.Sanitize(progress.Message, 140)}",
            "",
            "[Enter/Esc] 返回实例列表");
    }

    private static string FormatProgressBar(int? percent)
    {
        if (percent is not { } value)
        {
            return "░░░░░░░░░░░░  RUNNING / journal";
        }

        var bounded = Math.Clamp(value, 0, 100);
        var filled = bounded / 10;
        return $"{new string('█', filled)}{new string('░', 10 - filled)}  {bounded,3}%";
    }

    private string[] BuildProfilePreview() =>
    [
        "当前档案",
        TuiDisplayText.Sanitize(_dashboard.ProfileTitle.Replace("档案：", string.Empty, StringComparison.Ordinal), 40),
        $"发行版     {TuiDisplayText.Sanitize(_dashboard.DistroName, 32)}",
        $"VHDX       {(_dashboard.TargetConfigured ? "已配置" : "待配置")}",
        "",
        "按 Enter 刷新完整档案摘要。"
    ];

    private void ShowLogSelectionPreview()
    {
        CurrentPage = VelaWorkspacePage.Logs;
        _logSnapshot = null;
        _logAnalysis = null;
        _logEntries = [];
        _logLines = [];
        _logLevels = [];
        _evidencePanel.Visible = false;
        _logList.Visible = false;
        _homeView.Visible = false;
        _targetDetailView.Visible = false;
        _workspace.Visible = true;
        SetOverviewDecisionVisible(false);
        SetContentTitle("日志分析");
        _workspace.Text = string.Join(Environment.NewLine,
            "日志分析预览",
            "",
            "按 Enter 读取最新日志并分析。",
            "原文路径与命令输出只显示安全投影。"
        );
        SetNavigationStatus();
        _navigation.SetFocus();
        SetNeedsDraw();
    }

    private static string FormatNavigationLabel(MainMenuItem item, bool selected = false)
    {
        var label = item.Action switch
        {
            MainMenuAction.Preflight => "01  工作区",
            MainMenuAction.ExecuteCompaction => "02  执行压缩",
            MainMenuAction.OpenLogs => "02  日志归档",
            MainMenuAction.ManageProfiles => "03  目标档案",
            MainMenuAction.RecentRuns => "04  最近运行",
            MainMenuAction.Exit => "06  退出 Vela",
            _ => item.Label
        };
        return $"{(selected ? '>' : ' ')} {label}";
    }

    private void UpdateNavigationMarker()
    {
        if (_updatingNavigationLabels || _navigation.SelectedItem is not { } selected)
        {
            return;
        }

        _updatingNavigationLabels = true;
        try
        {
            for (var index = 0; index < _visibleMenuItems.Count; index++)
            {
                _navigationLabels[index] = FormatNavigationLabel(_visibleMenuItems[index], index == selected);
            }
        }
        finally
        {
            _updatingNavigationLabels = false;
        }
    }

    private string BuildHeader(string title, DashboardViewModel dashboard, AutomaticPreflightState state)
    {
        var overview = PreflightOverviewViewModel.Create(dashboard, state);
        var targets = PreflightOverviewFormatter.CreateTargetRows(
            overview,
            _selectedTargetIndex,
            _targetLocked);
        if (CurrentPage == VelaWorkspacePage.Logs || CurrentPage == VelaWorkspacePage.LogAnalysis)
        {
            return "VELA  ·  历史运行记录浏览";
        }

        if (CurrentPage == VelaWorkspacePage.ActionPreview)
        {
            var targetName = LockedTargetName ?? "未锁定目标";
            return _screenWidth < 110
                ? $"VELA  ·  ③ 影响评估  ·  {targetName}"
                : $"VELA  ·  ✔ 选择实例  ─  ✔ 环境预检  ─  ③ 影响评估  ·  {targetName}";
        }

        if (_targetLocked)
        {
            var lockedName = targets.FirstOrDefault(row => row.IsLocked)?.DistroName
                ?? TuiDisplayText.Sanitize(dashboard.DistroName, 32);
            var targetDetail = PreflightOverviewFormatter.CreateTargetDetail(
                overview,
                PreflightHomeViewModel.Create(
                    overview,
                    _selectedTargetIndex,
                    targetLocked: true));
            var preflightStatus = targetDetail.IsReady ? "✓" : "!";
            var statusLabel = targetDetail.IsReady ? "预检通过" : "预检需处理";
            if (_screenWidth < 72)
            {
                return $"VELA  ·  {preflightStatus} {lockedName}  ·  ② 环境预检";
            }

            return _screenWidth < 110
                ? $"VELA  ·  {preflightStatus} {lockedName} {statusLabel}  ·  ② 环境预检"
                : $"VELA  ·  {preflightStatus} {lockedName} {statusLabel}  ·  ① 选择实例 ✔  ─  ② 环境预检  ─  > 02 影响评估";
        }

        if (state.Status == AutomaticPreflightStatus.Checking)
        {
            return _screenWidth < 110
                ? "VELA  ·  ◌ 扫描中  ·  等待结果…"
                : "VELA  ·  ◌ 扫描中  ·  等待预检结果…";
        }

        var targetSummary = targets.Length == 0
            ? "! 未发现可用实例  ·  按 R 重扫"
            : $"ⓘ 发现 {targets.Length} 个实例  ·  ① 选择实例";
        if (_screenWidth < 72)
        {
            return $"VELA  ·  {targetSummary}";
        }

        return _screenWidth < 110
            ? $"VELA  ·  {targetSummary}  >  ② 环境预检"
            : $"VELA  ·  {targetSummary}  ─  ② 环境预检  ─  ③ 影响评估  ─  ④ 执行压缩";
    }

    private string BuildOverview(DashboardViewModel dashboard, AutomaticPreflightState state)
    {
        var home = PreflightHomeViewModel.Create(
            PreflightOverviewViewModel.Create(dashboard, state),
            _selectedTargetIndex,
            _targetLocked);
        return BuildTargetSelectionOverview(home, _screenWidth);
    }

    private static string BuildTargetSelectionOverview(
        PreflightHomeViewModel home,
        int screenWidth)
    {
        var selected = home.Targets.FirstOrDefault(row => row.IsSelected);
        if (screenWidth < VelaLayoutMetrics.NarrowContentWidth)
        {
            return TuiDisplayText.Sanitize(
                selected is null
                    ? "未发现实例 · [R] 重新扫描"
                    : $"目标 {selected.DistroName} · {selected.StatusText} · [↑↓] 切换 · [Enter] 锁定 · [R] 重扫",
                68);
        }

        var lines = new List<string>
        {
            home.Status switch
            {
                AutomaticPreflightStatus.Checking => "正在扫描 WSL 实例…",
                AutomaticPreflightStatus.Failed => "扫描失败 · 按 R 重试",
                _ => $"扫描完成，发现 {home.Targets.Length} 个 WSL 实例。"
            },
            "使用上下方向键选择目标，按 Enter 查看明细并锁定目标。",
            "",
            $"实例列表（{home.Targets.Length}）"
        };

        if (screenWidth >= 110)
        {
            lines.Add("发行版（Distro）       当前体积          VHDX 路径                 状态（Status）");
            lines.AddRange(home.Targets.Select(row =>
                $"{row.Selector} {TuiDisplayText.PadRight(row.DistroName, 22)} " +
                $"{TuiDisplayText.PadRight(row.CurrentSize, 16)} " +
                $"{TuiDisplayText.PadRight(row.VhdxPath, 24)} {row.StatusText}"));
        }
        else if (screenWidth >= 96)
        {
            lines.Add("发行版（Distro）                 当前体积          状态");
            lines.AddRange(home.Targets.Select(row =>
                $"{row.Selector} {TuiDisplayText.PadRight(row.DistroName, 30)} " +
                $"{TuiDisplayText.PadRight(row.CurrentSize, 16)} {row.StatusText}"));
        }
        else
        {
            lines.Add("发行版（Distro）                                      状态");
            lines.AddRange(home.Targets.Select(row =>
                $"{row.Selector} {TuiDisplayText.PadRight(row.DistroName, 40)} {row.StatusText}"));
        }

        return string.Join(Environment.NewLine, lines);
    }

    private void UpdateDecision(AutomaticPreflightState state)
    {
        _decision.Text = PreflightOverviewViewModel
            .Create(_dashboard, state)
            .Conclusion;
        _decision.SchemeName = VelaTerminalTheme.SchemeForPreflight(state.Status);
    }

    private void SetOverviewDecisionVisible(bool visible)
    {
        _decision.Visible = visible;
        _workspace.Y = visible ? (LayoutMode == VelaShellLayout.TwoPane ? 4 : 3) : 2;
        _homeView.Y = visible ? (LayoutMode == VelaShellLayout.TwoPane ? 4 : 3) : 2;
        ApplyContentRailLayout();
    }

    private void ApplyOverviewSurface()
    {
        var home = PreflightHomeViewModel.Create(
            Overview,
            _selectedTargetIndex,
            _targetLocked);
        _selectedTargetIndex = home.Targets.Length == 0
            ? -1
            : Math.Clamp(_selectedTargetIndex, 0, home.Targets.Length - 1);
        _homeView.Visible = true;
        _targetDetailView.Visible = false;
        _impactView.Visible = false;
        _workspace.Visible = false;
        _contentHeading.Visible = true;
        _homeView.Apply(PreflightHomeViewModel.Create(
            Overview,
            _selectedTargetIndex,
            _targetLocked));
        ApplyContentRailLayout();
    }

    private void ApplyTargetDetailSurface()
    {
        var home = PreflightHomeViewModel.Create(
            Overview,
            _selectedTargetIndex,
            _targetLocked);
        _selectedTargetIndex = home.Targets.Length == 0
            ? -1
            : Math.Clamp(_selectedTargetIndex, 0, home.Targets.Length - 1);
        CurrentPage = VelaWorkspacePage.TargetDetail;
        _homeView.Visible = false;
        _workspace.Visible = false;
        _targetDetailView.Visible = true;
        _impactView.Visible = false;
        _evidencePanel.Visible = false;
        _logList.Visible = false;
        _targetDetailView.Apply(
            PreflightOverviewFormatter.CreateTargetDetail(Overview, home));
        SetOverviewDecisionVisible(false);
        SetContentTitle("目标预检详情");
        _contentHeading.Visible = false;
        _header.Text = BuildHeader(_applicationTitle, _dashboard, PreflightState);
        SetNavigationStatus();
        _targetDetailView.SetFocus();
        SetNeedsDraw();
    }

    private void SyncLockedTarget()
    {
        if (!_targetLocked || string.IsNullOrWhiteSpace(_lockedTargetName))
        {
            return;
        }

        var rows = PreflightOverviewFormatter.CreateTargetRows(
            Overview,
            selectedTargetIndex: 0,
            targetLocked: false);
        var lockedIndex = -1;
        for (var index = 0; index < rows.Length; index++)
        {
            if (string.Equals(
                    rows[index].DistroName,
                    _lockedTargetName,
                    StringComparison.OrdinalIgnoreCase))
            {
                lockedIndex = index;
                break;
            }
        }

        if (lockedIndex < 0)
        {
            _targetLocked = false;
            _lockedTargetName = null;
            return;
        }

        _selectedTargetIndex = lockedIndex;
    }

    private void SetContentTitle(string title) =>
        _contentHeading.Text = TuiDisplayText.Sanitize(title, 32);

    private void SetNavigationStatus()
    {
        _status.Text = BuildActionHint();
    }

    private string BuildActionHint()
    {
        var compact = _screenWidth < 110;
        var hint = CurrentPage switch
        {
            VelaWorkspacePage.Overview when compact && _screenWidth < 72 => "[↑↓]实例 [R]重扫 [Esc]退出",
            VelaWorkspacePage.Overview when compact => "[↑↓]实例 [Enter]锁定并预检 [R]重扫 [Esc]退出",
            VelaWorkspacePage.TargetDetail when compact => "[Esc]重新选择 [Enter]影响评估",
            VelaWorkspacePage.Profiles when compact => "[↑↓]切换 [Enter]刷新 [Esc]返回",
            VelaWorkspacePage.RecentRuns when compact => "[↑↓]切换 [Enter]刷新 [Esc]返回",
            VelaWorkspacePage.Logs or VelaWorkspacePage.LogAnalysis when compact => "[↑↓]切换 [Enter]日志 [Esc]返回",
            VelaWorkspacePage.ActionPreview when compact => "[Esc]返回预检 [Y]确认执行压缩",
            VelaWorkspacePage.Confirmation when compact => "[Enter]确认 YES [Esc]取消",
            VelaWorkspacePage.Running when compact => "[执行中]journal 实时更新",
            VelaWorkspacePage.Result when compact => "[Enter/Esc]返回实例",
            VelaWorkspacePage.Overview when _navigation.HasFocus => "[↑↓]  导航菜单   [Enter]  执行当前项   [Tab]  选择实例   [Esc]  退出",
            VelaWorkspacePage.Overview => "[↑↓]  切换目标   [Enter]  锁定并预检   [R]  重新扫描   [Esc] 退出",
            VelaWorkspacePage.TargetDetail => "[Esc]  重新选择   [Enter]  进入影响评估",
            VelaWorkspacePage.Profiles => "[↑↓] 导航   [Enter] 刷新档案摘要   [Esc] 返回状态总览",
            VelaWorkspacePage.RecentRuns => "[↑↓] 导航   [Enter] 刷新运行记录   [Esc] 返回状态总览",
            VelaWorkspacePage.Logs or VelaWorkspacePage.LogAnalysis => "[↑↓] 导航   [Enter] 打开日志目录   [Esc] 返回状态总览",
            VelaWorkspacePage.ActionPreview => "[Esc]  返回预检   [Y]  确认执行压缩",
            VelaWorkspacePage.Confirmation => "[Enter] 确认 YES   [Esc] 取消",
            VelaWorkspacePage.Running => "[执行中] journal 实时更新   键盘输入已锁定",
            VelaWorkspacePage.Result => "[Enter/Esc] 返回实例列表",
            _ => "[↑↓] 导航   [Esc] 返回状态总览"
        };
        return $"导航 / 操作  {hint}";
    }

    private string BuildConfirmation(ConfirmationViewModel confirmation)
    {
        var promptLines = confirmation.Prompt
            .Split([Environment.NewLine, "\n"], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => TuiDisplayText.Sanitize(line, 120))
            .ToList();
        if (promptLines.Count > 0 && promptLines[^1].Contains("输入 YES", StringComparison.Ordinal))
        {
            promptLines.RemoveAt(promptLines.Count - 1);
        }

        var rows = new[] { "影响摘要" }
            .Concat(promptLines.Select(line => $"  {line}"))
            .Concat(["", "输入 YES 并按 Enter 确认；Esc 取消。"]);
        return string.Join(Environment.NewLine, rows);
    }

    private static string FormatLogLine(string text)
    {
        var fields = text.Split(' ', 6, StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length < 5 || !DateTimeOffset.TryParse(
                fields[1],
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind,
                out var timestamp))
        {
            return TuiDisplayText.Sanitize(text, 160);
        }

        var level = fields[2] switch
        {
            "Trace" => "TRACE",
            "Information" => "INFO",
            "Warning" => "WARN",
            "Error" => "ERROR",
            _ => "INFO"
        };
        var sequence = fields[0].Length >= 3 && fields[0][0] == '[' && fields[0][^1] == ']'
            && fields[0][1..^1].All(char.IsDigit)
            ? fields[0]
            : "[?]";
        var phase = TuiDisplayText.SafeToken(fields[3], 20, "未知阶段");
        var operation = TuiDisplayText.SafeToken(fields[4], 28, "未知事件");
        return $"{sequence,-4} {timestamp:HH:mm:ss}  {level,-5} {phase,-12} {operation}";
    }

    private string BuildLogAnalysis()
    {
        if (_logAnalysis is null)
        {
            return "暂无日志分析。按 R 重新读取日志。";
        }

        var width = LayoutMode == VelaShellLayout.TwoPane
            ? Math.Max(24, _screenWidth - 34)
            : Math.Max(24, _screenWidth - 6);
        var compact = _screenWidth < VelaLayoutMetrics.NarrowContentWidth;
        if (compact)
        {
            var focusLine = SelectLogEntries()
                .FirstOrDefault(line => line.Level == RunEventLevel.Error)
                ?? SelectLogEntries().FirstOrDefault(line => line.Level == RunEventLevel.Warning);
            var signal = _logAnalysis.ReadError is not null
                ? $"! {_logAnalysis.ReadError}"
                : !_logAnalysis.HasEntries
                    ? "○ 暂无条目 · 先运行只读预检"
                    : focusLine is not null
                        ? $"! {FormatCompactSignal(focusLine)} · 先查看"
                        : "✓ 未发现错误级记录 · 查看最新条目";
            return string.Join(
                Environment.NewLine,
                [
                    $"● 日志 {_logAnalysis.TotalCount} 条 · T{_logAnalysis.TraceCount} I{_logAnalysis.InformationCount} W{_logAnalysis.WarningCount} E{_logAnalysis.ErrorCount}",
                    TuiDisplayText.Sanitize($"{signal} · 最近 {_logAnalysis.LatestTimestamp}", width)
                ]);
        }

        var lines = new List<string>
        {
            _logAnalysis.ReadError is null ? "● 只读日志摘要" : $"! 读取状态  {_logAnalysis.ReadError}",
            _screenWidth < 100
                ? $"记录 {_logAnalysis.TotalCount} · T{_logAnalysis.TraceCount} I{_logAnalysis.InformationCount} W{_logAnalysis.WarningCount} E{_logAnalysis.ErrorCount}"
                : $"记录 {_logAnalysis.TotalCount,3}   TRACE {_logAnalysis.TraceCount,3}   INFO {_logAnalysis.InformationCount,3}   WARN {_logAnalysis.WarningCount,3}   ERROR {_logAnalysis.ErrorCount,3}",
            $"最近 {_logAnalysis.LatestTimestamp}   {_logAnalysis.LatestPhase} / {_logAnalysis.LatestOperation}",
            $"建议  {_logAnalysis.Recommendation}"
        };

        if (_logAnalysis.WasTailTruncated && !compact)
        {
            lines.Add("范围  仅分析日志文件尾部片段");
        }

        lines.Add("");
        lines.Add("下方显示已安全投影的最近日志条目。");
        return string.Join(Environment.NewLine, lines.Select(line => TuiDisplayText.Sanitize(line, width)));
    }

    private static string FormatCompactSignal(RunLogLine line)
    {
        var fields = line.Text.Split(' ', 6, StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length < 5)
        {
            return TuiDisplayText.Sanitize(line.Text, 48);
        }

        var level = fields[2] switch
        {
            "Trace" => "TRACE",
            "Information" => "INFO",
            "Warning" => "WARN",
            "Error" => "ERROR",
            _ => "INFO"
        };
        return $"{level} {TuiDisplayText.SafeToken(fields[3], 20, "未知阶段")}/{TuiDisplayText.SafeToken(fields[4], 24, "未知事件")}";
    }

    private void UpdateLogViewLayout()
    {
        var analysisPage = CurrentPage == VelaWorkspacePage.LogAnalysis;
        var compactAnalysis = analysisPage && _screenWidth < VelaLayoutMetrics.NarrowContentWidth;
        _logList.Visible = CurrentPage == VelaWorkspacePage.Logs || (analysisPage && !compactAnalysis);
        _logList.Width = ShouldShowEvidenceRail && analysisPage ? Dim.Percent(58) : Dim.Fill(1);
        _logList.Y = analysisPage ? 9 : 2;
    }

    private static RunLogLine[] BuildLogEntries(RunLogSnapshot snapshot)
    {
        if (snapshot.ErrorMessage is not null)
        {
            return
            [
                new RunLogLine(snapshot.ErrorMessage, RunEventLevel.Error),
                new RunLogLine("Enter 在文件管理器中打开日志目录。", RunEventLevel.Information)
            ];
        }

        var header = new[]
        {
            new RunLogLine(snapshot.WasTailTruncated ? "仅显示日志文件末尾内容。" : "最新运行日志：", RunEventLevel.Information),
            new RunLogLine("Enter 在文件管理器中打开日志目录。", RunEventLevel.Information)
        };
        return header.Concat(snapshot.Lines)
            .Take(20)
            .ToArray();
    }

    private void RefreshLogLines()
    {
        var entries = SelectLogEntries();
        _logLines = entries.Select(line => FormatLogLine(line.Text)).ToArray();
        _logLevels = entries.Select(line => line.Level).ToArray();
        _logList.SetSource(new ObservableCollection<string>(_logLines));
        if (_logLines.Length > 0)
        {
            _logList.SelectedItem = 0;
        }
    }

    private IReadOnlyList<RunLogLine> SelectLogEntries()
    {
        if (_screenWidth >= VelaLayoutMetrics.NarrowContentWidth || _availablePageRows >= 4)
        {
            return _logEntries;
        }

        var important = _logEntries
            .Where(line => line.Level is RunEventLevel.Error or RunEventLevel.Warning)
            .TakeLast(Math.Max(1, _availablePageRows))
            .ToArray();
        return important.Length > 0
            ? important
            : _logEntries.TakeLast(Math.Max(1, _availablePageRows)).ToArray();
    }

    private void UpdateEvidence()
    {
        if (CurrentPage == VelaWorkspacePage.LogAnalysis)
        {
            _evidencePanel.Title = "分析范围";
            _evidence.Text = BuildLogContext();
        }
        else
        {
            var overview = Overview;
            _evidencePanel.Title = "关键证据";
            var evidenceRows = overview.Gates
                .Take(3)
                .Select(gate => $"{gate.StatusLabel}  {gate.Label}  {gate.Detail}")
                .Concat([
                    "",
                    $"VHDX 文件  {overview.Evidence.FileSize}",
                    $"稀疏状态   {overview.Evidence.SparseState}",
                    $"宿主盘总容量 {overview.Evidence.HostTotalSize}",
                    $"宿主盘可用   {overview.Evidence.HostAvailableSpace}",
                    "",
                    "当前档案",
                    overview.ProfileName,
                    "风险判断以文字与符号同时表达。"]);
            _evidence.Text = string.Join(Environment.NewLine, evidenceRows);
        }

        ApplyContentRailLayout();
    }

    private bool ShouldShowEvidenceRail =>
        LayoutMode == VelaShellLayout.TwoPane
        && CurrentPage == VelaWorkspacePage.LogAnalysis
        && _screenWidth >= VelaLayoutMetrics.AnalysisRailWidth;

    private void ApplyContentRailLayout()
    {
        var showsEvidenceRail = ShouldShowEvidenceRail;
        var unifiedSurface = (CurrentPage is VelaWorkspacePage.Overview or VelaWorkspacePage.TargetDetail or VelaWorkspacePage.ActionPreview)
            && LayoutMode == VelaShellLayout.TwoPane;
        if (unifiedSurface)
        {
            // The homepage is a dashboard surface. Let the cards use the
            // available vertical space instead of compressing every fact into
            // one text block above a large empty area.
            if (_homeView.Visible || _targetDetailView.Visible || _impactView.Visible)
            {
                _contentPanel.Height = Dim.Fill(1);
            }
            else
            {
                var compactRows = _workspace.Text
                    .Split(Environment.NewLine, StringSplitOptions.None)
                    .Length + 4;
                _contentPanel.Height = Math.Min(
                    Math.Max(10, compactRows),
                    Math.Max(10, _screenHeight - 3));
            }
        }
        else if (LayoutMode == VelaShellLayout.TwoPane)
        {
            _contentPanel.Height = Dim.Fill(1);
        }
        _contentPanel.BorderStyle = unifiedSurface
            ? Terminal.Gui.Drawing.LineStyle.Single
            : Terminal.Gui.Drawing.LineStyle.None;
        _workspace.Width = showsEvidenceRail ? Dim.Percent(58) : Dim.Fill(1);
        _decision.Width = showsEvidenceRail ? Dim.Percent(58) : Dim.Fill(1);
        _homeView.X = 1;
        _homeView.Y = _decision.Visible
            ? (LayoutMode == VelaShellLayout.TwoPane ? 4 : 3)
            : 2;
        _homeView.Width = Dim.Fill(1);
        _homeView.Height = _confirmationInput.Visible ? Dim.Fill(2) : Dim.Fill();
        _targetDetailView.X = 1;
        _targetDetailView.Y = 2;
        _targetDetailView.Width = Dim.Fill(1);
        _targetDetailView.Height = _confirmationInput.Visible ? Dim.Fill(2) : Dim.Fill();
        _impactView.X = 1;
        _impactView.Y = 2;
        _impactView.Width = Dim.Fill(1);
        _impactView.Height = _confirmationInput.Visible ? Dim.Fill(2) : Dim.Fill();
        _evidencePanel.Visible = showsEvidenceRail;
        if (showsEvidenceRail)
        {
            _evidencePanel.X = Pos.Right(_workspace) + 1;
            _evidencePanel.Y = 2;
            _evidencePanel.Width = Dim.Fill();
            _evidencePanel.Height = 11;
        }
    }

    private string BuildLogContext()
    {
        if (_logAnalysis is null)
        {
            return "等待日志分析。";
        }

        return string.Join(Environment.NewLine,
            "读取范围",
            _logAnalysis.WasTailTruncated ? "文件尾部片段" : "当前运行窗口",
            $"可见条目   {_logAnalysis.TotalCount}",
            "投影字段",
            "序号 · 时间 · 级别",
            "阶段 · 事件",
            "原文已隐藏路径与命令输出。");
    }

    private string BuildPage()
    {
        var rowLimit = _availablePageRows;
        var lineWidth = LayoutMode == VelaShellLayout.TwoPane
            ? Math.Max(24, _screenWidth - (ShouldShowEvidenceRail ? (int)Math.Floor(_screenWidth * 0.26) : 28) - 10)
            : Math.Max(24, _screenWidth - 6);
        var visible = _pageLines
            .Take(rowLimit)
            .Select(line => TuiDisplayText.Sanitize(line, lineWidth))
            .ToList();
        if (_pageLines.Length > visible.Count)
        {
            visible.Add($"另有 {_pageLines.Length - visible.Count} 项；扩大终端查看。");
        }
        return string.Join(Environment.NewLine, visible);
    }

}

public sealed class TerminalGuiShellHost : IDisposable
{
    private readonly IApplication _application;
    private readonly VelaTerminalShell _shell;
    private bool _disposed;
    public TerminalGuiShellHost(IApplication application, VelaTerminalShell shell)
    {
        _application = application ?? throw new ArgumentNullException(nameof(application));
        _shell = shell ?? throw new ArgumentNullException(nameof(shell));
        _application.Keyboard.KeyDown += OnApplicationKeyDown;
        _application.ScreenChanged += OnScreenChanged;
        _shell.AdaptTo(_application.Screen);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _application.Keyboard.KeyDown -= OnApplicationKeyDown;
        _application.ScreenChanged -= OnScreenChanged;
        _disposed = true;
    }

    private void OnApplicationKeyDown(object? sender, Key key)
    {
        if (_shell.TryHandleRunLifecycleKey(key) ||
            _shell.TryHandleQuitKey(key) ||
            _shell.TryHandleFocusToggleKey(key) ||
            _shell.TryHandleTargetNavigationKey(key) ||
            _shell.TryHandleTargetDetailKey(key) ||
            _shell.TryHandleActionPreviewKey(key) ||
            _shell.TryHandleRefreshKey(key))
        {
            key.Handled = true;
        }
    }

    private void OnScreenChanged(object? sender, EventArgs eventArgs) => _shell.AdaptTo(_application.Screen);
}
