using Terminal.Gui.Drawing;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using Vela.Tui.Application;
using Vela.Tui.Rendering;

namespace Vela.Tui.Views;

/// <summary>
/// Detail surface shown after an instance is selected. It keeps the target
/// facts and the five execution gates in one vertical, keyboard-first view.
/// </summary>
public sealed class PreflightTargetDetailView : View
{
    private const int MaxChecks = 5;
    private readonly FrameView _statusPanel;
    private readonly Label _statusCode;
    private readonly Label _statusTitle;
    private readonly Label _statusSupport;
    private readonly FrameView _nextPanel;
    private readonly Label _nextStep;
    private readonly FrameView _targetPanel;
    private readonly DetailField[] _targetFields;
    private readonly FrameView _checksPanel;
    private readonly DetailCheckRow[] _checkRows;
    private readonly Label _blockerLabel;
    private PreflightTargetDetailViewModel? _detail;

    public PreflightTargetDetailView()
    {
        CanFocus = true;
        Width = Dim.Fill();
        Height = Dim.Fill();

        _statusPanel = CreatePanel(VelaTerminalTheme.InfoPanel);
        _statusPanel.Title = string.Empty;
        _statusCode = CreateLabel(VelaTerminalTheme.Info);
        _statusTitle = CreateLabel(VelaTerminalTheme.Base);
        _statusSupport = CreateLabel(VelaTerminalTheme.Muted);
        _nextPanel = CreatePanel(VelaTerminalTheme.Info);
        _nextStep = CreateLabel(VelaTerminalTheme.Info);
        _statusPanel.Add(_statusCode, _statusTitle, _statusSupport, _nextPanel);
        _nextPanel.Add(_nextStep);

        _targetPanel = CreatePanel(VelaTerminalTheme.Panel);
        _targetFields =
        [
            new DetailField("目标发行版"),
            new DetailField("当前体积"),
            new DetailField("VHDX 绝对路径"),
            new DetailField("实例锁定状态")
        ];
        foreach (var field in _targetFields)
        {
            _targetPanel.Add(field.Key, field.Leader, field.Value);
        }

        _checksPanel = CreatePanel(VelaTerminalTheme.Panel);
        _checkRows = Enumerable.Range(0, MaxChecks)
            .Select(_ => new DetailCheckRow())
            .ToArray();
        foreach (var row in _checkRows)
        {
            _checksPanel.Add(row.Symbol, row.Label, row.Leader, row.Status);
        }

        _blockerLabel = CreateLabel(VelaTerminalTheme.Base);
        _checksPanel.Add(_blockerLabel);
        Add(_statusPanel, _targetPanel, _checksPanel);
    }

    public void Apply(PreflightTargetDetailViewModel detail)
    {
        ArgumentNullException.ThrowIfNull(detail);
        _detail = detail;

        var ready = detail.IsReady;
        _statusPanel.SchemeName = ready
            ? VelaTerminalTheme.SuccessPanel
            : detail.StatusCode.StartsWith("×", StringComparison.Ordinal)
                ? VelaTerminalTheme.ErrorPanel
                : detail.StatusCode.StartsWith("!", StringComparison.Ordinal)
                    ? VelaTerminalTheme.AttentionPanel
                    : VelaTerminalTheme.InfoPanel;
        _statusCode.Text = detail.StatusCode;
        _statusCode.SchemeName = ready
            ? VelaTerminalTheme.SuccessStrong
            : detail.StatusCode.StartsWith("!", StringComparison.Ordinal)
                ? VelaTerminalTheme.AttentionStrong
                : detail.StatusCode.StartsWith("×", StringComparison.Ordinal)
                    ? VelaTerminalTheme.ErrorStrong
                    : VelaTerminalTheme.InfoStrong;
        _statusTitle.Text = BuildStatusTitle(detail);
        _statusTitle.SchemeName = VelaTerminalTheme.Base;
        _statusSupport.Text = detail.StatusSupport;
        _statusSupport.SchemeName = VelaTerminalTheme.Muted;
        _nextPanel.SchemeName = ready ? VelaTerminalTheme.Info : VelaTerminalTheme.Panel;
        _nextStep.Text = ready ? "下一步  [Enter] 预览压缩" : detail.NextStep;
        _nextStep.SchemeName = ready ? VelaTerminalTheme.Info : VelaTerminalTheme.Muted;

        _targetPanel.Title = "TARGET INFO  目标信息";
        var targetValues = new[]
        {
            detail.DistroName,
            string.IsNullOrWhiteSpace(detail.CurrentSize) ? "尚未读取" : detail.CurrentSize,
            string.IsNullOrWhiteSpace(detail.VhdxPath) ? "尚未读取" : detail.VhdxPath,
            FormatDisplayStatus(detail.FinalStatus)
        };
        for (var index = 0; index < _targetFields.Length; index++)
        {
            _targetFields[index].SetValue(targetValues[index], index == 2
                ? VelaTerminalTheme.Info
                : index == 3 && detail.IsReady
                    ? VelaTerminalTheme.Success
                    : VelaTerminalTheme.Base);
        }

        var passedChecks = detail.Checks.Count(check => check.Status == PreflightGateStatus.Matched);
        _checksPanel.Title = $"CHECK DETAILS  检查明细（{passedChecks}/{MaxChecks}）";
        for (var index = 0; index < _checkRows.Length; index++)
        {
            var row = _checkRows[index];
            if (index < detail.Checks.Length)
            {
                row.Visible = true;
                row.Apply(detail.Checks[index]);
            }
            else
            {
                row.Visible = false;
            }
        }

        _blockerLabel.Text = $"当前阻断项  {new string('·', 12)}  {detail.BlockerCount}";
        _blockerLabel.SchemeName = detail.BlockerCount == 0
            ? VelaTerminalTheme.Base
            : VelaTerminalTheme.Attention;
        SetNeedsLayout();
        SetNeedsDraw();
    }

    protected override void OnSubViewLayout(LayoutEventArgs args)
    {
        base.OnSubViewLayout(args);
        Arrange(Viewport.Width, Viewport.Height);
    }

    private void Arrange(int width, int height)
    {
        if (_detail is null || width < 24 || height < 2)
        {
            _statusPanel.Visible = false;
            _targetPanel.Visible = false;
            _checksPanel.Visible = false;
            return;
        }

        if (height < 5)
        {
            _statusPanel.Visible = true;
            _targetPanel.Visible = false;
            _checksPanel.Visible = false;
            if (height <= 2)
            {
                _statusPanel.BorderStyle = LineStyle.None;
                _statusPanel.Title = string.Empty;
                _nextPanel.Visible = false;
                _statusSupport.Visible = false;
                _statusTitle.Text = _detail.IsReady ? "5 项 PASS · 无阻断" : "预检需处理";
                Place(_statusPanel, 0, 0, width, height);
                Place(_statusCode, 1, 0, Math.Min(18, Math.Max(1, width - 2)), 1);
                Place(_statusTitle, Math.Min(20, Math.Max(1, width / 3)), 0, Math.Max(1, width - 22), 1);
                return;
            }

            _statusPanel.BorderStyle = LineStyle.Rounded;
            _statusPanel.Title = string.Empty;
            _statusSupport.Visible = true;
            Place(_statusPanel, 0, 0, width, height);
            ArrangeStatus(width, height, compact: true);
            return;
        }

        _statusPanel.Visible = true;
        _targetPanel.Visible = true;
        _statusPanel.BorderStyle = LineStyle.Rounded;
        _statusPanel.Title = string.Empty;
        _statusSupport.Visible = true;
        var compact = width < 72 || height < 22;
        var statusHeight = compact ? 4 : 6;
        var targetHeight = compact ? 8 : 13;
        var gap = compact ? 1 : 2;
        var targetY = statusHeight + gap;
        var checksY = targetY + targetHeight + gap;
        var checksHeight = Math.Max(6, height - checksY);

        Place(_statusPanel, 0, 0, width, statusHeight);
        Place(_targetPanel, 0, targetY, width, targetHeight);
        var showChecks = checksHeight >= (compact ? 8 : 10);
        _checksPanel.Visible = showChecks;
        if (showChecks)
        {
            Place(_checksPanel, 0, checksY, width, checksHeight);
        }
        ArrangeStatus(width, statusHeight, compact);
        ArrangeTarget(width, targetHeight, compact);
        if (showChecks)
        {
            ArrangeChecks(width, checksHeight, compact);
        }
    }

    private void ArrangeStatus(int width, int height, bool compact)
    {
        if (_detail is not null)
        {
            _statusTitle.Text = compact && width < 72
                ? _detail.IsReady ? "5 项 PASS · 无阻断" : "预检需处理"
                : BuildStatusTitle(_detail);
        }
        Place(_statusCode, 2, 1, compact ? 18 : 20, 1);
        var titleX = compact ? 21 : 22;
        var nextWidth = compact ? 0 : Math.Min(34, Math.Max(24, width / 4));
        var nextX = width - nextWidth - 2;
        var titleWidth = compact
            ? Math.Max(1, width - titleX - 2)
            : Math.Max(1, nextX - titleX - 1);
        Place(_statusTitle, titleX, 1, titleWidth, 1);
        Place(_statusSupport, titleX, Math.Min(2, height - 2), titleWidth, 1);
        _nextPanel.Visible = !compact && nextWidth >= 20;
        if (_nextPanel.Visible)
        {
            Place(_nextPanel, nextX, 1, nextWidth, 3);
            Place(_nextStep, 1, 0, Math.Max(1, nextWidth - 2), 1);
        }
    }

    private void ArrangeTarget(int width, int height, bool compact)
    {
        var valueX = compact ? Math.Max(20, width / 2) : Math.Max(28, width - 48);
        var valueWidth = Math.Max(1, width - valueX - 2);
        var leaderWidth = Math.Max(1, valueX - 22);
        for (var index = 0; index < _targetFields.Length; index++)
        {
            var y = compact ? 1 + index : 1 + index * 2;
            var field = _targetFields[index];
            field.Visible = y < height - 1;
            if (!field.Visible)
            {
                continue;
            }

            Place(field.Key, 2, y, 18, 1);
            field.Leader.Text = new string('·', leaderWidth);
            Place(field.Leader, 20, y, leaderWidth, 1);
            Place(field.Value, valueX, y, valueWidth, 1);
            field.AlignValue(valueWidth);
        }
    }

    private void ArrangeChecks(int width, int height, bool compact)
    {
        var statusWidth = Math.Min(12, Math.Max(8, width / 8));
        var statusX = Math.Max(1, width - statusWidth - 2);
        var labelX = 4;
        var leaderX = compact ? Math.Min(30, width / 3) : Math.Min(44, width / 2);
        var leaderWidth = Math.Max(1, statusX - leaderX - 1);
        for (var index = 0; index < _checkRows.Length; index++)
        {
            var row = _checkRows[index];
            var y = compact ? 1 + index : 1 + index * 2;
            row.Visible = index < (_detail?.Checks.Length ?? 0) && y < height - 2;
            if (!row.Visible)
            {
                continue;
            }

            Place(row.Symbol, 2, y, 2, 1);
            Place(row.Label, labelX, y, Math.Max(1, leaderX - labelX - 1), 1);
            row.Leader.Text = new string('·', leaderWidth);
            Place(row.Leader, leaderX, y, leaderWidth, 1);
            Place(row.Status, statusX, y, statusWidth, 1);
        }

        var blockerY = compact
            ? Math.Min(Math.Max(1, height - 3), 1 + _checkRows.Length)
            : Math.Min(Math.Max(1, height - 3), 1 + _checkRows.Length * 2);
        Place(_blockerLabel, 2, blockerY, Math.Max(1, width - 4), 1);
    }

    private static FrameView CreatePanel(string scheme) => new()
    {
        BorderStyle = LineStyle.Rounded,
        SchemeName = scheme,
        CanFocus = false
    };

    private static Label CreateLabel(string scheme) => new()
    {
        SchemeName = scheme,
        CanFocus = false
    };

    private static void Place(View view, int x, int y, int width, int height)
    {
        view.X = Math.Max(0, x);
        view.Y = Math.Max(0, y);
        view.Width = Math.Max(0, width);
        view.Height = Math.Max(0, height);
    }

    private sealed class DetailField
    {
        public DetailField(string label)
        {
            Key = CreateLabel(VelaTerminalTheme.Muted);
            Key.Text = label;
            Leader = CreateLabel(VelaTerminalTheme.Muted);
            Value = CreateLabel(VelaTerminalTheme.Base);
        }

        public Label Key { get; }
        public Label Leader { get; }
        public Label Value { get; }
        public bool Visible
        {
            get => Key.Visible;
            set => Key.Visible = Leader.Visible = Value.Visible = value;
        }

        private string _value = string.Empty;

        public void SetValue(string value, string scheme)
        {
            _value = value;
            Value.SchemeName = scheme;
            Value.Text = value;
        }

        public void AlignValue(int width) =>
            Value.Text = AlignRight(_value, width);
    }

    private sealed class DetailCheckRow
    {
        public Label Symbol { get; } = CreateLabel(VelaTerminalTheme.Success);
        public Label Label { get; } = CreateLabel(VelaTerminalTheme.Base);
        public Label Leader { get; } = CreateLabel(VelaTerminalTheme.Muted);
        public Label Status { get; } = CreateLabel(VelaTerminalTheme.Muted);
        public bool Visible
        {
            get => Label.Visible;
            set => Symbol.Visible = Label.Visible = Leader.Visible = Status.Visible = value;
        }

        public void Apply(PreflightTargetCheckViewModel check)
        {
            Symbol.Text = check.Symbol;
            Symbol.SchemeName = SchemeFor(check.Status);
            Label.Text = FormatDisplayCheckLabel(check.Label);
            Status.Text = check.StatusText;
            Status.SchemeName = Symbol.SchemeName;
        }

        private static string SchemeFor(PreflightGateStatus status) => status switch
        {
            PreflightGateStatus.Matched => VelaTerminalTheme.SuccessStrong,
            PreflightGateStatus.Attention => VelaTerminalTheme.AttentionStrong,
            PreflightGateStatus.Failed => VelaTerminalTheme.ErrorStrong,
            _ => VelaTerminalTheme.Muted
        };
    }

    private static string AlignRight(string value, int width)
    {
        var safe = TuiDisplayText.Sanitize(value, width);
        return new string(' ', Math.Max(0, width - safe.Length)) + safe;
    }

    private static string FormatDisplayStatus(string status) => status switch
    {
        "Ready ✓" => "READY ✓",
        "Running ⚠" => "RUNNING ⚠",
        "Blocked !" => "BLOCKED !",
        "Failed ×" => "FAILED ×",
        "Checking …" => "CHECKING …",
        _ => status
    };

    private static string FormatDisplayCheckLabel(string label) => label switch
    {
        "目标档案已读取" => "目标档案可读性",
        "VHDX 已配置" => "磁盘快照挂载点",
        "快照与日志可用" => "VHDX 文件系统结构",
        "发行版映射匹配" => "系统环境诊断",
        "无进程独占锁定" => "闲置状态校验",
        _ => label
    };

    private static string BuildStatusTitle(PreflightTargetDetailViewModel detail) => detail.StatusTitle;
}
