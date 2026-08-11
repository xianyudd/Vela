using System.Drawing;
using Terminal.Gui.Drawing;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using Vela.Tui.Application;
using Vela.Tui.Rendering;

namespace Vela.Tui.Views;

/// <summary>
/// Menu 01 is an instance picker first and a diagnostic surface second. The
/// view keeps the facts in a compact table so the user can make a target
/// decision without reading a paragraph assembled from internal state names.
/// </summary>
public sealed class PreflightHomeView : View
{
    private const int MaxVisibleRows = 16;
    private readonly FrameView _infoPanel;
    private readonly Label _infoPrefix;
    private readonly Label _infoTitle;
    private readonly Label _infoDetail;
    private readonly FrameView _tablePanel;
    private readonly Label _distroHeader;
    private readonly Label _sizeHeader;
    private readonly Label _pathHeader;
    private readonly Label _statusHeader;
    private readonly Label _tableDivider;
    private readonly Label _compactSummary;
    private readonly TargetRow[] _rows;
    private readonly Label _emptyState;
    private PreflightHomeViewModel _home = null!;

    public PreflightHomeView()
    {
        CanFocus = true;
        Width = Dim.Fill();
        Height = Dim.Fill();

        _infoPanel = CreatePanel();
        _infoPrefix = CreateLabel(VelaTerminalTheme.Info);
        _infoTitle = CreateLabel(VelaTerminalTheme.Base);
        _infoDetail = CreateLabel(VelaTerminalTheme.Info);
        _infoPanel.Add(_infoPrefix, _infoTitle, _infoDetail);

        _tablePanel = CreatePanel();
        _distroHeader = CreateLabel(VelaTerminalTheme.TableHeader);
        _sizeHeader = CreateLabel(VelaTerminalTheme.TableHeader);
        _pathHeader = CreateLabel(VelaTerminalTheme.TableHeader);
        _statusHeader = CreateLabel(VelaTerminalTheme.TableHeader);
        _tableDivider = CreateLabel(VelaTerminalTheme.Divider);
        _compactSummary = CreateLabel(VelaTerminalTheme.Info);
        _emptyState = CreateLabel(VelaTerminalTheme.Muted);
        _rows = Enumerable.Range(0, MaxVisibleRows).Select(_ => new TargetRow()).ToArray();

        _tablePanel.Add(
            _distroHeader,
            _sizeHeader,
            _pathHeader,
            _statusHeader,
            _tableDivider,
            _emptyState);
        foreach (var row in _rows)
        {
            _tablePanel.Add(
                row.SelectionBand,
                row.Marker,
                row.Distro,
                row.Size,
                row.Path,
                row.Status);
        }

        Add(_infoPanel, _tablePanel, _compactSummary);
    }

    public void Apply(PreflightHomeViewModel home)
    {
        ArgumentNullException.ThrowIfNull(home);
        _home = home;

        var infoScheme = home.Status switch
        {
            AutomaticPreflightStatus.Attention or AutomaticPreflightStatus.Stale => VelaTerminalTheme.Attention,
            AutomaticPreflightStatus.Failed => VelaTerminalTheme.Error,
            _ => VelaTerminalTheme.Info
        };
        _infoPanel.SchemeName = home.TargetLocked
            ? VelaTerminalTheme.SuccessPanel
            : home.Status switch
            {
                AutomaticPreflightStatus.Attention or AutomaticPreflightStatus.Stale => VelaTerminalTheme.AttentionPanel,
                AutomaticPreflightStatus.Failed => VelaTerminalTheme.ErrorPanel,
                _ => VelaTerminalTheme.InfoPanel
            };
        _infoPrefix.SchemeName = infoScheme switch
        {
            VelaTerminalTheme.Attention => VelaTerminalTheme.AttentionStrong,
            VelaTerminalTheme.Error => VelaTerminalTheme.ErrorStrong,
            _ => VelaTerminalTheme.InfoStrong
        };
        _infoPrefix.Text = home.TargetLocked ? "●  LOCKED" : "ⓘ  INFO";
        _infoTitle.Text = BuildInfoTitle(home);
        _infoTitle.SchemeName = home.TargetLocked ? VelaTerminalTheme.Success : VelaTerminalTheme.Base;
        _infoDetail.Text = BuildInfoDetail(home);
        _infoDetail.SchemeName = VelaTerminalTheme.Info;
        _compactSummary.Text = home.Targets.FirstOrDefault(row => row.IsSelected) is { } selected
            ? $"目标 {selected.DistroName} · {selected.StatusText}"
            : $"实例 {home.Targets.Length} 个 · 按 R 重新扫描";
        _compactSummary.SchemeName = home.Status switch
        {
            AutomaticPreflightStatus.Failed => VelaTerminalTheme.Error,
            AutomaticPreflightStatus.Attention or AutomaticPreflightStatus.Stale => VelaTerminalTheme.Attention,
            _ => VelaTerminalTheme.Info
        };

        _tablePanel.Title = $"实例列表（{home.Targets.Length}）";
        _emptyState.Text = home.Targets.Length == 0
            ? "未发现可选的 WSL 实例；按 R 重新扫描。"
            : string.Empty;
        _distroHeader.Text = "发行版（Distro）";
        _sizeHeader.Text = "当前体积";
        _pathHeader.Text = "VHDX 路径";
        _statusHeader.Text = "状态（Status）";

        for (var index = 0; index < _rows.Length; index++)
        {
            var row = _rows[index];
            if (index < home.Targets.Length)
            {
                row.Visible = true;
                row.Apply(home.Targets[index]);
            }
            else
            {
                row.Visible = false;
            }
        }

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
        if (_home is null || width < 20 || height < 2)
        {
            _infoPanel.Visible = false;
            _tablePanel.Visible = false;
            _compactSummary.Visible = false;
            return;
        }

        if (height < 8)
        {
            _infoPanel.Visible = false;
            _tablePanel.Visible = false;
            _compactSummary.Visible = true;
            Place(_compactSummary, 1, 0, Math.Max(1, width - 2), 1);
            return;
        }

        _infoPanel.Visible = true;
        _tablePanel.Visible = true;
        _compactSummary.Visible = false;
        var infoHeight = width < 48 ? 3 : width < 72 ? 4 : 5;
        var tableY = infoHeight + 2;
        var remaining = Math.Max(4, height - tableY);
        var desiredTableHeight = _home.Targets.Length == 0
            ? 5
            : 4 + Math.Min(MaxVisibleRows, _home.Targets.Length) * 2;
        var tableHeight = Math.Min(remaining, Math.Max(5, desiredTableHeight));

        Place(_infoPanel, 0, 0, width, infoHeight);
        Place(_tablePanel, 0, tableY, width, tableHeight);
        ArrangeInfo(width, infoHeight);
        ArrangeTable(width, tableHeight, showDetails: width >= 72);
    }

    private void ArrangeInfo(int width, int height)
    {
        var compact = width < 72;
        var prefixWidth = Math.Min(12, Math.Max(1, width - 4));
        var titleX = compact ? Math.Min(prefixWidth + 2, Math.Max(2, width / 3)) : 16;
        Place(_infoPrefix, 2, 1, prefixWidth, 1);
        Place(_infoTitle, titleX, 1, Math.Max(1, width - titleX - 2), 1);
        Place(_infoDetail, compact ? 2 : titleX, Math.Min(2, height - 1), Math.Max(1, width - (compact ? 4 : titleX + 2)), 1);
    }

    private void ArrangeTable(int width, int height, bool showDetails)
    {
        var statusWidth = Math.Min(20, Math.Max(10, width / 5));
        var statusX = Math.Max(1, width - statusWidth - 2);
        var distroX = 6;
        var sizeX = width >= 110 ? 37 : Math.Max(24, width / 3);
        var pathX = Math.Max(sizeX + 16, width >= 110 ? 54 : sizeX + 14);

        _distroHeader.Visible = showDetails;
        _sizeHeader.Visible = showDetails;
        _pathHeader.Visible = showDetails && width >= 96;
        _statusHeader.Visible = showDetails;
        _tableDivider.Visible = showDetails;
        _emptyState.Visible = _home.Targets.Length == 0;

        if (showDetails)
        {
            Place(_distroHeader, distroX, 1, Math.Max(1, sizeX - distroX - 2), 1);
            Place(_sizeHeader, sizeX, 1, Math.Max(1, pathX - sizeX - 2), 1);
            Place(_pathHeader, pathX, 1, Math.Max(1, statusX - pathX - 2), 1);
            Place(_statusHeader, statusX, 1, statusWidth, 1);
            _tableDivider.Text = new string('─', Math.Max(1, width - 2));
            Place(_tableDivider, 1, 2, Math.Max(1, width - 2), 1);
        }

        var firstRowY = showDetails ? 3 : 1;
        Place(_emptyState, 2, firstRowY, Math.Max(1, width - 4), 1);
        for (var index = 0; index < _rows.Length; index++)
        {
            var row = _rows[index];
            var y = showDetails ? firstRowY + index * 2 : firstRowY + index;
            row.Visible = index < _home.Targets.Length && y < height - 1;
            if (!row.Visible) continue;

            Place(row.Marker, 1, y, 3, 1);
            row.SelectionBand.Text = new string(' ', Math.Max(1, width - 2));
            Place(row.SelectionBand, 1, y, Math.Max(1, width - 2), 1);
            Place(row.Distro, distroX, y, Math.Max(1, sizeX - distroX - 2), 1);
            Place(row.Size, sizeX, y, Math.Max(1, pathX - sizeX - 2), 1);
            var pathWidth = Math.Max(1, statusX - pathX - 2);
            Place(row.Path, pathX, y, pathWidth, 1);
            row.SetPathWidth(pathWidth);
            Place(row.Status, statusX, y, statusWidth, 1);
            if (!showDetails)
            {
                row.Size.Visible = false;
                row.Path.Visible = false;
                row.Status.X = Math.Max(1, width - statusWidth - 2);
                row.Status.Width = statusWidth;
            }
            else
            {
                row.Size.Visible = true;
                row.Path.Visible = width >= 96;
                row.Status.Visible = true;
            }
        }
    }

    private static string BuildInfoTitle(PreflightHomeViewModel home)
    {
        if (home.TargetLocked && home.Targets.FirstOrDefault(row => row.IsLocked) is { } locked)
        {
            return $"目标已锁定：{locked.DistroName}";
        }

        return home.Status switch
        {
            AutomaticPreflightStatus.Checking => "正在扫描 WSL 实例…",
            AutomaticPreflightStatus.Failed => "扫描失败，请按 R 重试。",
            _ when home.Targets.Length == 0 => "未发现可用的 WSL 实例。",
            _ => $"扫描完成，发现 {home.Targets.Length} 个 WSL 实例。"
        };
    }

    private static string BuildInfoDetail(PreflightHomeViewModel home)
    {
        if (home.TargetLocked)
        {
            return "已锁定目标；按 R 重新扫描，或按 Esc 返回实例列表。";
        }

        var detail = "请使用 ↑↓ 选择目标，←→ 切换步骤，按 Enter 查看详细预检报告。";
        return home.Status is AutomaticPreflightStatus.Attention or AutomaticPreflightStatus.Failed &&
            !string.IsNullOrWhiteSpace(home.StatusReason)
            ? $"{detail} 关注项：{home.StatusReason}"
            : detail;
    }

    private static FrameView CreatePanel() => new()
    {
        BorderStyle = LineStyle.Rounded,
        SchemeName = VelaTerminalTheme.Panel,
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

    private sealed class TargetRow
    {
        private bool _isSelected;
        private string _pathValue = string.Empty;

        public Label SelectionBand { get; } = CreateLabel(VelaTerminalTheme.Selection);
        public Label Marker { get; } = CreateLabel(VelaTerminalTheme.Muted);
        public Label Distro { get; } = CreateLabel(VelaTerminalTheme.Base);
        public Label Size { get; } = CreateLabel(VelaTerminalTheme.Base);
        public Label Path { get; } = CreateLabel(VelaTerminalTheme.Muted);
        public Label Status { get; } = CreateLabel(VelaTerminalTheme.Muted);

        public bool Visible
        {
            get => Distro.Visible;
            set
            {
                SelectionBand.Visible = value && _isSelected;
                Marker.Visible = Distro.Visible = Size.Visible = Path.Visible = Status.Visible = value;
            }
        }

        public void Apply(PreflightTargetRowViewModel row)
        {
            Marker.Text = row.IsLocked ? "◆" : row.Selector;
            Distro.Text = row.DistroName;
            Size.Text = row.CurrentSize;
            _pathValue = row.VhdxPath;
            Path.Text = _pathValue;
            Status.Text = row.StatusText;
            _isSelected = row.IsSelected;
            SelectionBand.Visible = row.IsSelected && Distro.Visible;
            SelectionBand.SchemeName = VelaTerminalTheme.Selection;

            var rowScheme = row.IsSelected ? VelaTerminalTheme.Selection : VelaTerminalTheme.Base;
            Marker.SchemeName = row.IsSelected ? VelaTerminalTheme.Selection : VelaTerminalTheme.Muted;
            Distro.SchemeName = rowScheme;
            Size.SchemeName = rowScheme;
            Path.SchemeName = row.IsSelected ? VelaTerminalTheme.Selection : VelaTerminalTheme.Muted;
            Status.SchemeName = SchemeFor(row.Status);
        }

        public void SetPathWidth(int width) =>
            Path.Text = PreflightOverviewFormatter.FormatVhdxPath(_pathValue, width);

        private static string SchemeFor(PreflightTargetRowStatus status) => status switch
        {
            PreflightTargetRowStatus.Ready => VelaTerminalTheme.SuccessStrong,
            PreflightTargetRowStatus.Running => VelaTerminalTheme.AttentionStrong,
            PreflightTargetRowStatus.Attention => VelaTerminalTheme.AttentionStrong,
            PreflightTargetRowStatus.Failed => VelaTerminalTheme.ErrorStrong,
            _ => VelaTerminalTheme.Muted
        };
    }
}
