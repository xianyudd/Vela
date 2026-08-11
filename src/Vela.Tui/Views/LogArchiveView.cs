using Terminal.Gui.Drawing;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using Vela.Core.Models;
using Vela.Tui.Application;
using Vela.Tui.Rendering;

namespace Vela.Tui.Views;

/// <summary>
/// The design稿's LOG_LIST surface: a compact, keyboard-first history table.
/// The view owns only layout and selection visuals; reading a selected run is
/// kept in the program coordinator so the log surface remains read-only.
/// </summary>
public sealed class LogArchiveView : View
{
    private const int MaxVisibleRows = 20;
    private readonly FrameView _tablePanel;
    private readonly Label _dateHeader;
    private readonly Label _targetHeader;
    private readonly Label _reclaimedHeader;
    private readonly Label _statusHeader;
    private readonly Label _divider;
    private readonly Label _emptyState;
    private readonly ArchiveRow[] _rows;
    private RunHistoryEntry[] _entries = [];
    private int _selectedIndex;

    public LogArchiveView()
    {
        CanFocus = true;
        Width = Dim.Fill();
        Height = Dim.Fill();

        _tablePanel = new FrameView
        {
            BorderStyle = LineStyle.Rounded,
            SchemeName = VelaTerminalTheme.Panel,
            CanFocus = false
        };
        _dateHeader = CreateLabel(VelaTerminalTheme.TableHeader);
        _targetHeader = CreateLabel(VelaTerminalTheme.TableHeader);
        _reclaimedHeader = CreateLabel(VelaTerminalTheme.TableHeader);
        _statusHeader = CreateLabel(VelaTerminalTheme.TableHeader);
        _divider = CreateLabel(VelaTerminalTheme.Divider);
        _emptyState = CreateLabel(VelaTerminalTheme.Muted);
        _rows = Enumerable.Range(0, MaxVisibleRows).Select(_ => new ArchiveRow()).ToArray();

        _tablePanel.Add(
            _dateHeader,
            _targetHeader,
            _reclaimedHeader,
            _statusHeader,
            _divider,
            _emptyState);
        foreach (var row in _rows)
        {
            _tablePanel.Add(
                row.SelectionBand,
                row.Marker,
                row.Date,
                row.Target,
                row.Reclaimed,
                row.Status);
        }

        Add(_tablePanel);
    }

    public int SelectedIndex => _selectedIndex;

    public RunHistoryEntry? SelectedEntry =>
        _selectedIndex >= 0 && _selectedIndex < _entries.Length
            ? _entries[_selectedIndex]
            : null;

    public void Apply(RunHistorySnapshot snapshot, int selectedIndex = 0)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        _entries = snapshot.Entries.Take(MaxVisibleRows).ToArray();
        _selectedIndex = _entries.Length == 0
            ? -1
            : Math.Clamp(selectedIndex, 0, _entries.Length - 1);
        _tablePanel.Title = $"日志归档（{_entries.Length}）";
        _dateHeader.Text = "执行时间 (UTC+8)";
        _targetHeader.Text = "目标实例";
        _reclaimedHeader.Text = "释放空间";
        _statusHeader.Text = "最终状态";
        _emptyState.Text = snapshot.ErrorMessage is { Length: > 0 } error
            ? TuiDisplayText.Sanitize(error, 96)
            : _entries.Length == 0
                ? "暂无历史运行记录。"
                : string.Empty;

        for (var index = 0; index < _rows.Length; index++)
        {
            var row = _rows[index];
            if (index < _entries.Length)
            {
                row.Visible = true;
                row.Apply(_entries[index], index == _selectedIndex);
            }
            else
            {
                row.Visible = false;
            }
        }

        SetNeedsLayout();
        SetNeedsDraw();
    }

    public bool MoveSelection(int direction)
    {
        if (_entries.Length == 0 || direction == 0)
        {
            return false;
        }

        var next = Math.Clamp(_selectedIndex + Math.Sign(direction), 0, _entries.Length - 1);
        if (next == _selectedIndex)
        {
            return false;
        }

        _selectedIndex = next;
        for (var index = 0; index < _entries.Length; index++)
        {
            _rows[index].SetSelected(index == _selectedIndex);
        }

        SetNeedsDraw();
        return true;
    }

    protected override void OnSubViewLayout(LayoutEventArgs args)
    {
        base.OnSubViewLayout(args);
        Arrange(Viewport.Width, Viewport.Height);
    }

    private void Arrange(int width, int height)
    {
        if (width < 24 || height < 4)
        {
            _tablePanel.Visible = false;
            return;
        }

        _tablePanel.Visible = true;
        Place(_tablePanel, 0, 0, width, height);

        var showDetails = width >= 72;
        var statusWidth = showDetails
            ? Math.Min(18, Math.Max(10, width / 6))
            : Math.Min(10, Math.Max(8, width / 4));
        var statusX = Math.Max(1, width - statusWidth - 2);
        var dateX = showDetails ? 7 : 5;
        var targetX = showDetails
            ? width >= 110 ? 31 : Math.Max(24, width / 3)
            : dateX;
        var reclaimX = showDetails
            ? width >= 110 ? 58 : Math.Max(targetX + 16, width * 2 / 3)
            : statusX;

        _dateHeader.Visible = _targetHeader.Visible = _reclaimedHeader.Visible = _statusHeader.Visible = showDetails;
        _divider.Visible = showDetails;
        _emptyState.Visible = _entries.Length == 0;
        if (showDetails)
        {
            Place(_dateHeader, dateX, 1, Math.Max(1, targetX - dateX - 2), 1);
            Place(_targetHeader, targetX, 1, Math.Max(1, reclaimX - targetX - 2), 1);
            Place(_reclaimedHeader, reclaimX, 1, Math.Max(1, statusX - reclaimX - 2), 1);
            Place(_statusHeader, statusX, 1, statusWidth, 1);
            _divider.Text = new string('─', Math.Max(1, width - 2));
            Place(_divider, 1, 2, Math.Max(1, width - 2), 1);
        }

        var firstRowY = showDetails ? 3 : 1;
        Place(_emptyState, 2, firstRowY, Math.Max(1, width - 4), 1);
        for (var index = 0; index < _rows.Length; index++)
        {
            var row = _rows[index];
            var y = firstRowY + index * (showDetails ? 2 : 1);
            row.Visible = index < _entries.Length && y < height - 1;
            if (!row.Visible)
            {
                continue;
            }

            Place(row.SelectionBand, 1, y, Math.Max(1, width - 2), 1);
            Place(row.Marker, 1, y, 4, 1);
            Place(row.Date, dateX, y, Math.Max(1, targetX - dateX - 2), 1);
            Place(row.Target, targetX, y, Math.Max(1, reclaimX - targetX - 2), 1);
            Place(row.Reclaimed, reclaimX, y, Math.Max(1, statusX - reclaimX - 2), 1);
            Place(row.Status, statusX, y, statusWidth, 1);
            row.SetWidths(
                Math.Max(1, targetX - dateX - 2),
                Math.Max(1, reclaimX - targetX - 2),
                Math.Max(1, statusX - reclaimX - 2),
                statusWidth,
                showDetails);
        }
    }

    private static string FormatTimestamp(DateTimeOffset? timestamp) =>
        timestamp?.ToLocalTime().ToString(
            "yyyy-MM-dd HH:mm:ss",
            System.Globalization.CultureInfo.InvariantCulture)
        ?? "未知时间";

    private static string FormatReclaimed(RunHistoryEntry entry) =>
        entry.ReclaimedBytes is { } bytes
            ? PreflightOverviewFormatter.FormatCapacity(bytes)
            : "--";

    private static string FormatStatus(RunHistoryEntry entry) =>
        entry.IsMalformed || entry.TerminalResult is null
            ? "INVALID"
            : entry.TerminalResult is TerminalResult.Succeeded or TerminalResult.CompletedWithNoReclaim
                ? "SUCCESS"
                : "FAILED";

    private static string SchemeFor(RunHistoryEntry entry) =>
        entry.IsMalformed || entry.TerminalResult is not (TerminalResult.Succeeded or TerminalResult.CompletedWithNoReclaim)
            ? VelaTerminalTheme.Error
            : VelaTerminalTheme.Success;

    private static Label CreateLabel(string scheme) => new()
    {
        SchemeName = scheme,
        CanFocus = false
    };

    private static void Place(View view, int x, int y, int width, int height)
    {
        view.X = Math.Max(0, x);
        view.Y = Math.Max(0, y);
        view.Width = Math.Max(1, width);
        view.Height = Math.Max(1, height);
    }

    private sealed class ArchiveRow
    {
        private bool _selected;
        private RunHistoryEntry? _entry;
        private int _dateWidth;
        private int _targetWidth;
        private int _reclaimedWidth;
        private int _statusWidth;
        private bool _showDetails;

        public Label SelectionBand { get; } = CreateLabel(VelaTerminalTheme.Selection);
        public Label Marker { get; } = CreateLabel(VelaTerminalTheme.Muted);
        public Label Date { get; } = CreateLabel(VelaTerminalTheme.Base);
        public Label Target { get; } = CreateLabel(VelaTerminalTheme.Base);
        public Label Reclaimed { get; } = CreateLabel(VelaTerminalTheme.Base);
        public Label Status { get; } = CreateLabel(VelaTerminalTheme.Success);

        public ArchiveRow()
        {
            Status.TextAlignment = Alignment.End;
        }

        public bool Visible
        {
            get => Date.Visible;
            set
            {
                SelectionBand.Visible = value && _selected;
                Marker.Visible = Date.Visible = Target.Visible = Reclaimed.Visible = Status.Visible = value;
            }
        }

        public void Apply(RunHistoryEntry entry, bool selected)
        {
            _entry = entry;
            _selected = selected;
            Marker.Text = selected ? "❯" : string.Empty;
            Date.Text = FormatTimestamp(entry.StartedAtUtc);
            Target.Text = TuiDisplayText.Sanitize(
                entry.DistroName ?? entry.ProfileDisplayName,
                48);
            Reclaimed.Text = FormatReclaimed(entry);
            Status.Text = FormatStatus(entry);
            SetSelected(selected);
            ApplyWidths();
        }

        public void SetSelected(bool selected)
        {
            _selected = selected;
            Marker.Text = selected ? "❯" : string.Empty;
            SelectionBand.Visible = selected && Date.Visible;
            var rowScheme = selected ? VelaTerminalTheme.Selection : VelaTerminalTheme.Base;
            Marker.SchemeName = selected ? VelaTerminalTheme.Selection : VelaTerminalTheme.Muted;
            Date.SchemeName = rowScheme;
            Target.SchemeName = rowScheme;
            Reclaimed.SchemeName = rowScheme;
            Status.SchemeName = _entry is null ? VelaTerminalTheme.Muted : SchemeFor(_entry);
            ApplyWidths();
        }

        public void SetWidths(
            int dateWidth,
            int targetWidth,
            int reclaimedWidth,
            int statusWidth,
            bool showDetails)
        {
            _dateWidth = dateWidth;
            _targetWidth = targetWidth;
            _reclaimedWidth = reclaimedWidth;
            _statusWidth = statusWidth;
            _showDetails = showDetails;
            ApplyWidths();
        }

        private void ApplyWidths()
        {
            if (_entry is null)
            {
                return;
            }

            Date.Text = _showDetails
                ? TuiDisplayText.PadRight(FormatTimestamp(_entry.StartedAtUtc), _dateWidth)
                : string.Empty;
            Target.Text = TuiDisplayText.PadRight(
                TuiDisplayText.Sanitize(
                    _entry.DistroName ?? _entry.ProfileDisplayName,
                    Math.Max(1, _targetWidth)),
                _targetWidth);
            Reclaimed.Text = TuiDisplayText.PadRight(FormatReclaimed(_entry), _reclaimedWidth);
            Status.Text = TuiDisplayText.PadRight(FormatStatus(_entry), _statusWidth);
            if (!_showDetails)
            {
                Date.Visible = false;
                Reclaimed.Visible = false;
            }
        }
    }
}
