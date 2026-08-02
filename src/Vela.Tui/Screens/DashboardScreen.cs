using System.Collections.Immutable;
using Spectre.Console;
using Vela.Core.Contracts;
using Vela.Core.Models;
using Vela.Core.Workflows;
using Vela.Tui.Menu;
using Vela.Tui.Rendering;
using CoreValidationResult = Vela.Core.Validation.ValidationResult;
using Profile = Vela.Core.Models.Profile;

namespace Vela.Tui.Screens;

public interface IVelaConsole
{
    void RenderMenu(MainMenuViewModel viewModel);

    void RenderDashboard(DashboardViewModel viewModel);

    void RenderProgress(RunProgressViewModel viewModel);
}

public sealed record DashboardViewModel(
    string ApplicationTitle,
    string ProfileTitle,
    string DistroName,
    string VhdxPath,
    string RegistryMapping,
    VhdxSnapshot? VhdxSnapshot,
    ImmutableArray<string> RunningDistros,
    ImmutableArray<string> Notices,
    string? ErrorMessage,
    string? RunDirectory)
{
    public static DashboardViewModel CreateInitial(Profile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        return new DashboardViewModel(
            MainMenu.ApplicationTitle,
            $"Profile: {profile.DisplayName}",
            profile.DistroName,
            profile.VhdxPath,
            "尚未运行预检",
            null,
            ImmutableArray<string>.Empty,
            ImmutableArray<string>.Empty,
            null,
            null);
    }

    public static DashboardViewModel FromWorkflow(WorkflowResult workflowResult)
    {
        ArgumentNullException.ThrowIfNull(workflowResult);

        var preflight = workflowResult.Preflight;
        var runningDistros = preflight.RunningInventory?.Distributions
            .Where(static distribution => distribution.State == WslDistributionState.Running)
            .Select(static distribution => distribution.Name)
            .ToImmutableArray() ?? ImmutableArray<string>.Empty;
        var notices = workflowResult.Diagnostics
            .Where(static diagnostic => diagnostic.Level is RunEventLevel.Trace or RunEventLevel.Information or RunEventLevel.Warning)
            .Select(static diagnostic => diagnostic.Message)
            .ToImmutableArray();
        var error = workflowResult.Diagnostics
            .FirstOrDefault(static diagnostic => diagnostic.Level == RunEventLevel.Error)
            ?.Message;

        return new DashboardViewModel(
            MainMenu.ApplicationTitle,
            $"Profile: {workflowResult.Summary.Profile.DisplayName}",
            workflowResult.Summary.Profile.DistroName,
            workflowResult.Summary.Profile.VhdxPath,
            preflight.LxssResolution?.ResolvedVhdxPath ?? "未找到注册表映射",
            preflight.VhdxInspection?.Snapshot,
            runningDistros,
            notices,
            error,
            workflowResult.RunDirectory);
    }
}

public sealed class DashboardScreen
{
    private readonly IVelaConsole _console;

    public DashboardScreen(IVelaConsole console)
    {
        ArgumentNullException.ThrowIfNull(console);
        _console = console;
    }

    public void Render(DashboardViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        _console.RenderDashboard(viewModel);
    }
}

public interface IPreflightViewModelSource
{
    DashboardViewModel Create(Profile profile);
}

public sealed class PreflightScreen
{
    private readonly DashboardScreen _dashboard;
    private readonly RunRenderer _renderer;
    private readonly IPreflightViewModelSource _source;

    public PreflightScreen(
        DashboardScreen dashboard,
        RunRenderer renderer,
        IPreflightViewModelSource source)
    {
        ArgumentNullException.ThrowIfNull(dashboard);
        ArgumentNullException.ThrowIfNull(renderer);
        ArgumentNullException.ThrowIfNull(source);

        _dashboard = dashboard;
        _renderer = renderer;
        _source = source;
    }

    public void Render(Profile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        _renderer.Render(new RunProgressViewModel(
            RunProgressState.Preflighting,
            "正在渲染只读预检预览。",
            Percent: null));
        _dashboard.Render(_source.Create(profile));
        _renderer.Render(new RunProgressViewModel(
            RunProgressState.Succeeded,
            "只读预检预览已显示。",
            Percent: 100));
    }
}

public sealed class InMemoryPreflightViewModelSource : IPreflightViewModelSource
{
    public DashboardViewModel Create(Profile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var capturedAtUtc = DateTimeOffset.UtcNow;
        var installedInventory = new WslInventory(
            capturedAtUtc,
            ImmutableArray.Create(
                new WslDistribution(
                    profile.DistroName,
                    WslDistributionState.Stopped,
                    Version: 2,
                    IsDefault: true)));
        var snapshot = new VhdxSnapshot(
            capturedAtUtc,
            profile.VhdxPath,
            0,
            capturedAtUtc,
            null,
            new DriveSnapshot(
                Path.GetPathRoot(profile.VhdxPath) ?? profile.VhdxPath,
                0,
                0));
        var report = new PreflightReport(
            CoreValidationResult.Valid,
            installedInventory,
            new LxssProfileResolution(
                LxssResolutionStatus.Matched,
                profile.DistroName,
                profile.VhdxPath,
                profile.VhdxPath),
            new VhdxInspectionResult(VhdxInspectionStatus.Succeeded, snapshot),
            new WslInventory(capturedAtUtc, ImmutableArray<WslDistribution>.Empty));
        var result = new WorkflowResult(
            new RunSummary(
                profile.Id,
                profile,
                OperationIntent.Preflight,
                capturedAtUtc,
                capturedAtUtc,
                BeforeSnapshot: snapshot,
                AfterSnapshot: null,
                TerminalResult: TerminalResult.Succeeded),
            report,
            ImmutableArray.Create(
                new WorkflowDiagnostic(
                    WorkflowDiagnosticCode.SparseStateUnknown,
                    RunPhase.Snapshot,
                    RunEventLevel.Warning,
                    "当前显示内存预览；未调用 WSL、注册表、VHDX 或 journal 适配器。")),
            RunDirectory: null);

        return DashboardViewModel.FromWorkflow(result);
    }
}

public sealed class SpectreConsoleAdapter : IVelaConsole
{
    private readonly IAnsiConsole _console;

    public SpectreConsoleAdapter(IAnsiConsole console)
    {
        ArgumentNullException.ThrowIfNull(console);
        _console = console;
    }

    public void RenderMenu(MainMenuViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        _console.Write(new Rule($"[bold blue]{Escape(viewModel.Title)}[/]"));
        foreach (var item in viewModel.Items)
        {
            _console.MarkupLine($"[green]•[/] {Escape(item.Label)}");
        }
    }

    public void RenderDashboard(DashboardViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        var snapshot = viewModel.VhdxSnapshot;
        var vhdxSnapshot = snapshot is null
            ? "尚未采集"
            : $"{snapshot.FileLengthBytes:N0} bytes; {snapshot.LastWriteUtc:O}";
        var driveSnapshot = snapshot is null
            ? "尚未采集"
            : $"{snapshot.Drive.RootPath}; 可用 {snapshot.Drive.AvailableFreeSpaceBytes:N0} / {snapshot.Drive.TotalSizeBytes:N0} bytes";
        var sparse = snapshot?.IsSparse switch
        {
            true => "是",
            false => "否",
            null => "未知"
        };
        var runningDistros = viewModel.RunningDistros.IsDefaultOrEmpty
            ? "未发现运行中的发行版"
            : string.Join(", ", viewModel.RunningDistros);
        var notices = viewModel.Notices.IsDefaultOrEmpty
            ? "无"
            : string.Join(Environment.NewLine, viewModel.Notices);

        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("[bold]预检字段[/]")
            .AddColumn("[bold]值[/]");

        table.AddRow("档案", Escape(viewModel.ProfileTitle));
        table.AddRow("发行版", Escape(viewModel.DistroName));
        table.AddRow("VHDX", Escape(viewModel.VhdxPath));
        table.AddRow("注册表映射", Escape(viewModel.RegistryMapping));
        table.AddRow("VHDX 快照", Escape(vhdxSnapshot));
        table.AddRow("驱动器快照", Escape(driveSnapshot));
        table.AddRow("稀疏", Escape(sparse));
        table.AddRow("运行中的发行版", Escape(runningDistros));
        table.AddRow("通知", Escape(notices));
        table.AddRow("错误", Escape(viewModel.ErrorMessage ?? "无"));
        table.AddRow("日志目录", Escape(viewModel.RunDirectory ?? "尚未创建运行目录"));

        _console.Write(new Rule($"[bold blue]{Escape(viewModel.ApplicationTitle)}[/]"));
        _console.Write(table);
    }

    public void RenderProgress(RunProgressViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        var percentage = viewModel.Percent is int value ? $" ({value}%)" : string.Empty;
        _console.MarkupLine(
            $"[bold yellow]{Escape(viewModel.State.ToString())}[/] {Escape(viewModel.Message)}{Escape(percentage)}");
    }

    private static string Escape(string value) => Markup.Escape(value);
}
