using System.Collections.Immutable;
using Spectre.Console;
using Vela.Core.Contracts;
using Vela.Tui.Application;
using Vela.Windows.Diagnostics;
using Profile = Vela.Core.Models.Profile;

namespace Vela.Tui.Menu;

public enum MainMenuAction
{
    Preflight,
    ExecuteCompaction,
    ManageProfiles,
    RecentRuns,
    OpenLogs,
    Exit
}

public sealed record MainMenuItem(MainMenuAction Action, string Label);

public sealed record MainMenuViewModel(
    string Title,
    ImmutableArray<MainMenuItem> Items);

public sealed record ConfirmationViewModel(
    string Prompt,
    string RequiredInput,
    ImmutableArray<string> RunningDistros,
    bool AcceptsSingleKey = false);

public sealed class MainMenu
{
    public const string ApplicationTitle = "Vela — WSL VHDX Compact";

    private static readonly ImmutableArray<MainMenuItem> MenuItems =
        ImmutableArray.Create(
            new MainMenuItem(MainMenuAction.Preflight, "预检结果"),
            new MainMenuItem(MainMenuAction.ExecuteCompaction, "执行压缩"),
            new MainMenuItem(MainMenuAction.ManageProfiles, "管理目标档案"),
            new MainMenuItem(MainMenuAction.RecentRuns, "查看最近运行记录"),
            new MainMenuItem(MainMenuAction.OpenLogs, "打开日志目录"),
            new MainMenuItem(MainMenuAction.Exit, "退出"));

    public MainMenu() => ViewModel = new MainMenuViewModel(ApplicationTitle, MenuItems);

    public MainMenuViewModel ViewModel { get; }

    public static ConfirmationViewModel CreateFirstRunConfirmation(AppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return new ConfirmationViewModel(
            $"首次启动需要初始化 Vela 数据目录。{Environment.NewLine}" +
            "将创建缺失的配置、待处理请求和日志目录；已有文件不会覆盖。" +
            $"{Environment.NewLine}输入 YES 继续；其他输入将退出。",
            "YES",
            ImmutableArray<string>.Empty);
    }

    public static ConfirmationViewModel CreateRepairConfirmation(
        bool configurationExists,
        bool pendingDirectoryExists,
        bool logsDirectoryExists)
    {
        var missing = new List<string>();
        if (!configurationExists)
        {
            missing.Add("配置");
        }

        if (!pendingDirectoryExists)
        {
            missing.Add("待处理请求目录");
        }

        if (!logsDirectoryExists)
        {
            missing.Add("日志目录");
        }

        var missingSummary = missing.Count == 0
            ? "数据目录需要安全检查。"
            : $"将补齐缺失的：{string.Join("、", missing)}。";
        return new ConfirmationViewModel(
            $"Vela 数据目录尚未完整。{Environment.NewLine}" +
            missingSummary +
            $"{Environment.NewLine}已有文件不会覆盖。输入 YES 继续；其他输入将退出。",
            "YES",
            ImmutableArray<string>.Empty);
    }

    public static ConfirmationViewModel CreateExecuteConfirmation(
        Profile profile,
        ImmutableArray<WslDistribution> distributions,
        string? dataRootDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var runningDistros = distributions.IsDefault
            ? ImmutableArray<string>.Empty
            : distributions
                .Where(static distribution => distribution.State == WslDistributionState.Running)
                .Select(static distribution => distribution.Name)
                .ToImmutableArray();
        var runningDistroSummary = runningDistros.IsDefaultOrEmpty
            ? "当前没有运行中的发行版。"
            : $"运行中的发行版：{BoundedList(runningDistros, "、", 96)}。";
        var dataRootSummary = string.IsNullOrWhiteSpace(dataRootDirectory)
            ? "数据根目录：未指定。"
            : "数据根目录：已配置。";
        var impactSummary = profile.ShutdownMode switch
        {
            Vela.Core.Models.ShutdownMode.Global => "影响：将停止全部正在运行的 WSL 发行版后再执行压缩。",
            Vela.Core.Models.ShutdownMode.Distro => $"影响：将停止目标发行版 {TuiDisplayText.Sanitize(profile.DistroName, 64)} 后再执行压缩。",
            _ => "影响：执行前会按当前停止范围处理 WSL 发行版。"
        };

        return new ConfirmationViewModel(
            $"即将对发行版“{TuiDisplayText.Sanitize(profile.DistroName, 64)}”执行压缩。{Environment.NewLine}" +
            $"来源档案：{TuiDisplayText.Sanitize(profile.DisplayName, 64)}{Environment.NewLine}" +
            $"停止范围：{GetShutdownModeLabel(profile.ShutdownMode)}{Environment.NewLine}" +
            $"VHDX 路径：{TuiDisplayText.PathStatus(profile.VhdxPath)}{Environment.NewLine}" +
            $"{runningDistroSummary}{Environment.NewLine}" +
            $"{impactSummary}{Environment.NewLine}" +
            $"{dataRootSummary}{Environment.NewLine}" +
            "按 Y 再次确认执行。",
            "Y",
            runningDistros,
            AcceptsSingleKey: true);
    }
    private static string BoundedList(
        IEnumerable<string> values,
        string separator,
        int maxCells)
    {
        var result = string.Empty;
        foreach (var value in values.Take(20))
        {
            var item = TuiDisplayText.Sanitize(value, 48);
            var candidate = result.Length == 0
                ? item
                : result + separator + item;
            var bounded = TuiDisplayText.Sanitize(candidate, maxCells);
            if (!string.Equals(candidate, bounded, StringComparison.Ordinal))
            {
                return bounded;
            }

            result = candidate;
        }

        return result;
    }

    private static string GetShutdownModeLabel(Vela.Core.Models.ShutdownMode mode) => mode switch
    {
        Vela.Core.Models.ShutdownMode.Global => "全局停止范围",
        Vela.Core.Models.ShutdownMode.Distro => "目标发行版停止范围",
        _ => "未知停止范围"
    };
    public static bool IsConfirmationAccepted(
        ConfirmationViewModel viewModel,
        string? response)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        return viewModel.AcceptsSingleKey
            ? string.Equals(response, viewModel.RequiredInput, StringComparison.OrdinalIgnoreCase)
            : string.Equals(response, viewModel.RequiredInput, StringComparison.Ordinal);
    }
}
