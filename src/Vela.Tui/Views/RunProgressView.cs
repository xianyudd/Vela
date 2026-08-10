using System.Collections.ObjectModel;
using Terminal.Gui.Drawing;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using Vela.Tui.Application;
using Vela.Tui.Rendering;

namespace Vela.Tui.Views;

/// <summary>
/// The STEP2_RUNNING surface from the interactive design: one progress rail
/// and one terminal-like log stream.  Durable journal text remains the source
/// of every log row.
/// </summary>
public sealed class RunProgressView : View
{
    private readonly Label _title;
    private readonly Label _target;
    private readonly Label _percent;
    private readonly FrameView _progressPanel;
    private readonly Label _progressBar;
    private readonly FrameView _consolePanel;
    private readonly ListView _console;
    private string[] _logLines = [];

    public RunProgressView()
    {
        CanFocus = false;
        Width = Dim.Fill();
        Height = Dim.Fill();

        _title = CreateLabel(VelaTerminalTheme.Surface);
        _title.Text = "Optimizing VHDX Block Allocations ▪";

        _target = CreateLabel(VelaTerminalTheme.Muted);
        _percent = CreateLabel(VelaTerminalTheme.Info);
        _percent.TextAlignment = Alignment.End;

        _progressPanel = new FrameView
        {
            BorderStyle = LineStyle.Single,
            SchemeName = VelaTerminalTheme.SurfacePanel,
            CanFocus = false
        };
        _progressBar = CreateLabel(VelaTerminalTheme.Info);

        _consolePanel = new FrameView
        {
            Title = "Console Log · LIVE",
            BorderStyle = LineStyle.Single,
            SchemeName = VelaTerminalTheme.LogPanel,
            CanFocus = false
        };
        _console = new ListView
        {
            X = 1,
            Y = 0,
            Width = Dim.Fill(1),
            Height = Dim.Fill(1),
            SchemeName = VelaTerminalTheme.LogPanel,
            CanFocus = false
        };
        _console.RowRender += (_, args) =>
        {
            if (args.Row < 0 || args.Row >= _logLines.Length)
            {
                return;
            }

            var line = _logLines[args.Row];
            var scheme = line.Contains("ERROR", StringComparison.OrdinalIgnoreCase)
                ? VelaTerminalTheme.Error
                : line.Contains("WARN", StringComparison.OrdinalIgnoreCase)
                    ? VelaTerminalTheme.Attention
                    : VelaTerminalTheme.Success;
            args.RowAttribute = VelaTerminalTheme.NormalAttribute(scheme);
        };
        _consolePanel.Add(_console);

        Add(_title, _target, _percent, _progressPanel, _progressBar, _consolePanel);
    }

    public void Apply(RunProgressViewModel progress)
    {
        ArgumentNullException.ThrowIfNull(progress);

        var target = TuiDisplayText.Sanitize(progress.TargetName, 64);
        var path = TuiDisplayText.Sanitize(progress.VhdxPath, 120);
        _title.Text = progress.State == RunProgressState.Running
            ? "Optimizing VHDX Block Allocations ▪"
            : FormatTerminalTitle(progress.State);
        _consolePanel.Title = progress.State == RunProgressState.Running
            ? "Console Log · LIVE"
            : "Console Log · FINAL";
        _target.Text = string.IsNullOrWhiteSpace(target)
            ? "Target: 未锁定"
            : string.IsNullOrWhiteSpace(path)
                ? $"Target: {target}"
                : $"Target: {target}  ·  VHDX: {TuiDisplayText.Sanitize(path, 88)}";
        _percent.Text = progress.Percent is { } percent
            ? $"{Math.Clamp(percent, 0, 100),3}%"
            : progress.State == RunProgressState.Running
                ? "…"
                : "100%";
        _progressBar.Text = BuildProgressBar(progress.Percent, 72);

        _logLines = BuildLogLines(progress);
        _console.SetSource(new ObservableCollection<string>(_logLines));
        if (_logLines.Length > 0)
        {
            _console.SelectedItem = _logLines.Length - 1;
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
        var contentWidth = Math.Max(24, Math.Min(106, width - 4));
        var contentX = Math.Max(0, (width - contentWidth) / 2);
        var compact = width < 72 || height < 18;
        var progressHeight = compact ? 2 : 3;
        var consoleY = compact ? 5 : 7;
        _title.Text = width < 72
            ? "VHDX OPTIMIZING ▪"
            : "Optimizing VHDX Block Allocations ▪";

        Place(_title, contentX, 0, Math.Max(1, contentWidth - 12), 1);
        Place(_percent, contentX + Math.Max(1, contentWidth - 10), 0, 10, 1);
        Place(_target, contentX, 1, contentWidth, 1);
        Place(_progressPanel, contentX, 3, contentWidth, progressHeight);
        Place(_progressBar, contentX + 2, 4, Math.Max(1, contentWidth - 4), 1);
        Place(_consolePanel, contentX, consoleY, contentWidth, Math.Max(5, height - consoleY - 1));
    }

    private static string[] BuildLogLines(RunProgressViewModel progress)
    {
        var target = TuiDisplayText.Sanitize(progress.TargetName, 64);
        var lines = progress.VisibleLogLines
            .Select(line => TuiDisplayText.Sanitize(line, 160))
            .ToList();
        if (lines.Count == 0)
        {
            lines.Add(string.IsNullOrWhiteSpace(target)
                ? "[INFO] journal stream: no events yet"
                : $"[INFO] Target: {target}");
            if (!string.IsNullOrWhiteSpace(progress.Message))
            {
                lines.Add($"[INFO] {TuiDisplayText.Sanitize(progress.Message, 140)}");
            }
        }
        else if (!string.IsNullOrWhiteSpace(target) &&
                 !lines.Any(line => line.Contains(target, StringComparison.OrdinalIgnoreCase)))
        {
            lines.Insert(0, $"[INFO] Target: {target}");
        }

        return lines.TakeLast(18).ToArray();
    }

    private static string BuildProgressBar(int? percent, int width)
    {
        var bounded = Math.Clamp(percent ?? 0, 0, 100);
        var slots = Math.Max(10, width - 10);
        var filled = bounded * slots / 100;
        return $"{new string('█', filled)}{new string('░', slots - filled)}  {bounded,3}%";
    }

    private static string FormatTerminalTitle(RunProgressState state) => state switch
    {
        RunProgressState.Succeeded => "Physical Compaction Complete",
        RunProgressState.Cancelled => "Compaction Cancelled",
        RunProgressState.TimedOut => "Compaction Timed Out",
        RunProgressState.ReadFailed => "Journal Read Failed",
        _ => "Compaction Failed"
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
        view.Width = Math.Max(1, width);
        view.Height = Math.Max(1, height);
    }
}
