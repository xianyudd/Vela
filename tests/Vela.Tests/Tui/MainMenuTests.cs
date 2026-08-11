using System.Collections.Immutable;
using Spectre.Console;
using Vela.Core.Contracts;
using Vela.Core.Models;
using Vela.Tui;
using Vela.Tui.Application;
using Vela.Tui.Menu;
using Vela.Tui.Rendering;
using Profile = Vela.Core.Models.Profile;

namespace Vela.Tests.Tui;

public sealed class MainMenuTests
{
    [Fact]
    public void CreateFirstRunConfirmation_HidesPathsAndRequiresExactYes()
    {
        var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vela-first-run-" + Guid.NewGuid().ToString("N"));
        var paths = new Vela.Windows.Diagnostics.AppPaths(root);

        var confirmation = MainMenu.CreateFirstRunConfirmation(paths);

        Assert.Contains("首次启动", confirmation.Prompt, StringComparison.Ordinal);
        Assert.Contains("配置", confirmation.Prompt, StringComparison.Ordinal);
        Assert.DoesNotContain(root, confirmation.Prompt, StringComparison.Ordinal);
        Assert.DoesNotContain(paths.ConfigurationFilePath, confirmation.Prompt, StringComparison.Ordinal);
        Assert.DoesNotContain('', confirmation.Prompt);
        Assert.True(MainMenu.IsConfirmationAccepted(confirmation, "YES"));
        Assert.False(MainMenu.IsConfirmationAccepted(confirmation, "yes"));
    }

    [Fact]
    public void FrameRenderer_NarrowWidth_TruncatesLongPathsAndUsesChineseLabels()
    {
        var profile = CreateProfile() with { VhdxPath = @"D:\very-long-folder\another-folder\target.vhdx" };
        var dashboard = DashboardViewModel.CreateInitial(profile) with
        {
            InspectionState = TargetInspectionState.Available,
            VhdxEvidence = new VhdxEvidenceViewModel(
                1024,
                DateTimeOffset.UnixEpoch,
                null,
                4096,
                2048)
        };
        var frame = new TuiFrameViewModel(
            new MainMenu().ViewModel,
            dashboard,
            new RunProgressViewModel(RunProgressState.Succeeded, "完成", 100));

        var markup = new FrameRenderer().BuildMarkup(frame, FrameRenderer.NarrowTerminalWidth);

        Assert.Contains("字节", markup, StringComparison.Ordinal);
        Assert.Contains("状态", markup, StringComparison.Ordinal);
        Assert.Contains("VHDX", markup, StringComparison.Ordinal);
        Assert.Contains("已配置", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("very-long-folder", markup, StringComparison.Ordinal);
        Assert.DoesNotContain('', markup);
    }

    [Fact]
    public void ViewModel_ContainsTheSixExpectedMenuLabels()
    {
        var menu = new MainMenu();

        Assert.Equal(
            new[]
            {
                "预检结果",
                "执行压缩",
                "管理目标档案",
                "查看最近运行记录",
                "日志归档",
                "退出"
            },
            menu.ViewModel.Items.Select(static item => item.Label));
        Assert.Equal("Vela — WSL VHDX Compact", menu.ViewModel.Title);
    }

    [Fact]
    public void MenuFactory_ProvidesStableViewModel()
    {
        var menu = new MainMenu();

        Assert.Equal(
            new[]
            {
                "预检结果",
                "执行压缩",
                "管理目标档案",
                "查看最近运行记录",
                "日志归档",
                "退出"
            },
            menu.ViewModel.Items.Select(static item => item.Label));
        Assert.Equal("Vela — WSL VHDX Compact", menu.ViewModel.Title);
    }

    [Fact]
    public void FrameRenderer_BuildMarkupIncludesDashboardFieldsAndEscapesDynamicText()
    {
        var profile = CreateProfile();
        var dashboard = DashboardViewModel.CreateInitial(profile) with
        {
            ProfileTitle = "Profile [test]",
            MappingState = TargetMappingState.Matched,
            InspectionState = TargetInspectionState.Available,
            VhdxEvidence = new VhdxEvidenceViewModel(
                1024,
                DateTimeOffset.UnixEpoch,
                true,
                4096,
                2048),
            RunningDistros = ImmutableArray.Create("Ubuntu-24.04"),
            Notices = ImmutableArray.Create("notice [escaped]"),
            ErrorMessage = "error [escaped]",
            LogsAvailable = true
        };
        var frame = new TuiFrameViewModel(
            new MainMenu().ViewModel,
            dashboard,
            new RunProgressViewModel(RunProgressState.Failed, "failed [message]", 40));

        var markup = new FrameRenderer().BuildMarkup(frame);

        Assert.Contains("驱动器", markup, StringComparison.Ordinal);
        Assert.Contains("稀疏", markup, StringComparison.Ordinal);
        Assert.Contains("是", markup, StringComparison.Ordinal);
        Assert.Contains("Profile", markup, StringComparison.Ordinal);
        Assert.Contains("test", markup, StringComparison.Ordinal);
        Assert.Contains("failed", markup, StringComparison.Ordinal);
        Assert.Contains("message", markup, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateExecuteConfirmation_RequiresSecondYAndShowsRunningDistros()
    {
        var profile = CreateProfile();
        var confirmation = MainMenu.CreateExecuteConfirmation(
            profile,
            ImmutableArray.Create(
                new WslDistribution("Ubuntu-24.04", WslDistributionState.Running, 2, true),
                new WslDistribution("docker-desktop", WslDistributionState.Running, 2, false)));

        Assert.Equal("Y", confirmation.RequiredInput);
        Assert.True(confirmation.AcceptsSingleKey);
        Assert.Contains("按 Y 再次确认执行", confirmation.Prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("YES", confirmation.Prompt, StringComparison.Ordinal);
        Assert.Contains("Ubuntu-24.04", confirmation.Prompt, StringComparison.Ordinal);
        Assert.Contains("docker-desktop", confirmation.Prompt, StringComparison.Ordinal);
        Assert.Equal(new[] { "Ubuntu-24.04", "docker-desktop" }, confirmation.RunningDistros);
    }

    [Fact]
    public void CreateExecuteConfirmation_ShowsScopeTargetDataRootAndImpact()
    {
        var profile = CreateProfile();
        var dataRootDirectory = @"C:\Users\Vela\AppData\Local\Vela";

        var confirmation = MainMenu.CreateExecuteConfirmation(
            profile,
            ImmutableArray.Create(
                new WslDistribution("Ubuntu-24.04", WslDistributionState.Running, 2, true)),
            dataRootDirectory);

        Assert.Contains("全局停止范围", confirmation.Prompt, StringComparison.Ordinal);
        Assert.Contains("VHDX 路径：已配置", confirmation.Prompt, StringComparison.Ordinal);
        Assert.Contains("数据根目录：已配置", confirmation.Prompt, StringComparison.Ordinal);
        Assert.DoesNotContain(profile.VhdxPath, confirmation.Prompt, StringComparison.Ordinal);
        Assert.DoesNotContain(dataRootDirectory, confirmation.Prompt, StringComparison.Ordinal);
        Assert.Contains("影响", confirmation.Prompt, StringComparison.Ordinal);
        Assert.Contains("Ubuntu-24.04", confirmation.Prompt, StringComparison.Ordinal);
        Assert.Equal("Y", confirmation.RequiredInput);
    }

    [Fact]
    public void CreateExecuteConfirmation_identifies_the_locked_distribution_as_the_operation_target()
    {
        var selectedTarget = CreateProfile() with
        {
            DistroName = "docker-desktop",
            VhdxPath = @"D:\Docker\wsl\data\ext4.vhdx",
            ShutdownMode = ShutdownMode.Distro
        };

        var confirmation = MainMenu.CreateExecuteConfirmation(
            selectedTarget,
            ImmutableArray<WslDistribution>.Empty);

        Assert.Contains("发行版“docker-desktop”", confirmation.Prompt, StringComparison.Ordinal);
        Assert.Contains("来源档案：", confirmation.Prompt, StringComparison.Ordinal);
        Assert.Contains("目标发行版停止范围", confirmation.Prompt, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Y", true)]
    [InlineData("y", true)]
    [InlineData("YES", false)]
    [InlineData(null, false)]
    public void IsConfirmationAccepted_accepts_only_the_second_y_for_compaction(
        string? response,
        bool expected)
    {
        var confirmation = MainMenu.CreateExecuteConfirmation(
            CreateProfile(),
            ImmutableArray<WslDistribution>.Empty);

        Assert.Equal(expected, MainMenu.IsConfirmationAccepted(confirmation, response));
    }

    [Theory]
    [InlineData("YES", true)]
    [InlineData("yes", false)]
    [InlineData("YES ", false)]
    [InlineData(null, false)]
    public void IsConfirmationAccepted_RequiresTheExactYesToken(string? response, bool expected)
    {
        var confirmation = new ConfirmationViewModel(
            "输入 YES 继续。",
            "YES",
            ImmutableArray<string>.Empty);

        Assert.Equal(expected, MainMenu.IsConfirmationAccepted(confirmation, response));
    }

    [Theory]
    [InlineData(40, false)]
    [InlineData(79, false)]
    [InlineData(80, true)]
    [InlineData(119, true)]
    [InlineData(120, true)]
    [InlineData(160, true)]
    public void FrameRenderer_WidthBands_PreserveStatusAndAdaptNavigation(
        int width,
        bool showsNavigation)
    {
        var frame = new TuiFrameViewModel(
            new MainMenu().ViewModel,
            DashboardViewModel.CreateInitial(CreateProfile()),
            new RunProgressViewModel(
                RunProgressState.Succeeded,
                "只读预检已完成。",
                100));

        var markup = new FrameRenderer().BuildMarkup(frame, width, terminalHeight: 30);

        Assert.Contains("Ubuntu-24.04", markup, StringComparison.Ordinal);
        Assert.Contains("只读预检已完成", markup, StringComparison.Ordinal);
        Assert.Equal(
            showsNavigation,
            markup.Contains("执行压缩", StringComparison.Ordinal));
    }

    [Fact]
    public void FrameRenderer_LowHeight_BoundsRecentRows()
    {
        var entries = Enumerable.Range(0, 8)
            .Select(index => new RecentRunListItemViewModel(
                DateTimeOffset.UnixEpoch.AddMinutes(index),
                $"PROFILE-{index}",
                OperationIntent.Preflight,
                TerminalResult.Succeeded,
                ReclaimedBytes: index,
                IsMalformed: false,
                ErrorMessage: null))
            .ToImmutableArray();
        var frame = new TuiFrameViewModel(
            new MainMenu().ViewModel,
            DashboardViewModel.CreateInitial(CreateProfile()),
            new RunProgressViewModel(RunProgressState.Succeeded, "已加载。", 100),
            page: new RecentRunsPageViewModel(entries, 0, null));

        var markup = new FrameRenderer().BuildMarkup(frame, 100, terminalHeight: 18);

        Assert.Contains("PROFILE-0", markup, StringComparison.Ordinal);
        Assert.Contains("PROFILE-3", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("PROFILE-4", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("PROFILE-7", markup, StringComparison.Ordinal);
        Assert.Contains("Esc 返回", markup, StringComparison.Ordinal);
    }

    [Fact]
    public void FrameRenderer_HostileText_StripsEscapeSequencesControlsAndEscapesMarkup()
    {
        var dashboard = DashboardViewModel.CreateInitial(CreateProfile()) with
        {
            ProfileTitle = "档案 [hostile] \u001b[31m红色\u001b[0m",
            Notices = ImmutableArray.Create("通知\u0001内容"),
            ErrorMessage = "\u001b]8;;https://secret.example\u0007链接\u001b]8;;\u0007"
        };
        var frame = new TuiFrameViewModel(
            new MainMenu().ViewModel,
            dashboard,
            new RunProgressViewModel(
                RunProgressState.Failed,
                "失败 [detail] \u001b[2J清屏",
                null));

        var markup = new FrameRenderer().BuildMarkup(frame, 120, terminalHeight: 30);

        Assert.DoesNotContain('\u001b', markup);
        Assert.DoesNotContain('\u0001', markup);
        Assert.DoesNotContain("https://secret.example", markup, StringComparison.Ordinal);
        Assert.Contains("[[hostile]]", markup, StringComparison.Ordinal);
        Assert.Contains("[[detail]]", markup, StringComparison.Ordinal);
        Assert.Contains("红色", markup, StringComparison.Ordinal);
        Assert.Contains("链接", markup, StringComparison.Ordinal);
    }

    [Fact]
    public void FrameRenderer_CjkAndCombiningText_TruncatesByDisplayCells()
    {
        var dashboard = DashboardViewModel.CreateInitial(CreateProfile()) with
        {
            ProfileTitle = string.Concat(Enumerable.Repeat("档案", 40)) + "e\u0301"
        };
        var frame = new TuiFrameViewModel(
            new MainMenu().ViewModel,
            dashboard,
            new RunProgressViewModel(RunProgressState.Idle, "等待。", null));

        var markup = new FrameRenderer().BuildMarkup(frame, 40, terminalHeight: 20);

        Assert.Contains("…", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("�", markup, StringComparison.Ordinal);
        Assert.Contains("状态", markup, StringComparison.Ordinal);
    }

    [Fact]
    public void FrameRenderer_ProfileAndRecentPages_DoNotExposeSensitiveIdentifiers()
    {
        const string rawPath = @"D:\secret\target.vhdx";
        var runId = Guid.Parse("e0d6d9f3-9ec2-43b5-9f90-76d949d17f08");
        var profile = CreateProfile() with { VhdxPath = rawPath };
        var profilePage = new ProfileListPageViewModel(
            new ProfileManagementViewModel(
                ImmutableArray.Create(new ProfileListItemViewModel(
                    "目标档案",
                    "Ubuntu-24.04",
                    TargetConfigured: true,
                    ShutdownMode.Global,
                    TimeSpan.FromSeconds(45),
                    IsCurrent: true,
                    IsSelected: true)),
                0,
                "档案管理"));
        var recentPage = new RecentRunDetailPageViewModel(
            IsMalformed: false,
            "目标档案",
            OperationIntent.Compact,
            TerminalResult.Succeeded,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch.AddSeconds(1),
            TimeSpan.FromSeconds(1),
            ReclaimedBytes: 1,
            LogsAvailable: true,
            ErrorMessage: null);
        var baseFrame = new TuiFrameViewModel(
            new MainMenu().ViewModel,
            DashboardViewModel.CreateInitial(profile),
            new RunProgressViewModel(RunProgressState.Succeeded, "完成。", 100));
        var renderer = new FrameRenderer();

        var profileMarkup = renderer.BuildMarkup(baseFrame with { Page = profilePage }, 120);
        var recentMarkup = renderer.BuildMarkup(baseFrame with { Page = recentPage }, 120);

        Assert.DoesNotContain(rawPath, profileMarkup, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(rawPath, recentMarkup, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(runId.ToString(), profileMarkup, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(runId.ToString(), recentMarkup, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("VHDX 已配置", profileMarkup, StringComparison.Ordinal);
        Assert.Contains("日志", recentMarkup, StringComparison.Ordinal);
        Assert.Contains("可打开", recentMarkup, StringComparison.Ordinal);
    }

    [Fact]
    public void FrameRenderer_RenderRedirected_WritesOneSafeFrameWithoutClearing()
    {
        const string rawPath = @"D:\private\redirected-target.vhdx";
        const string oscSecret = "redirected-exception-secret";
        var runId = Guid.Parse("e0d6d9f3-9ec2-43b5-9f90-76d949d17f08");
        var profile = CreateProfile() with { VhdxPath = rawPath };
        var dashboard = DashboardViewModel.CreateInitial(profile) with
        {
            ProfileTitle = "目标 [安全] \u001b[31m红色\u001b[0m",
            ErrorMessage = $"\u001b]8;;{oscSecret}\u0007受控错误\u001b]8;;\u0007"
        };
        var frame = new TuiFrameViewModel(
            new MainMenu().ViewModel,
            dashboard,
            new RunProgressViewModel(
                RunProgressState.Failed,
                "只读输出 \u001b[2J不会清屏",
                null),
            page: new RecentRunDetailPageViewModel(
                IsMalformed: false,
                "目标档案",
                OperationIntent.Preflight,
                TerminalResult.CompletedWithNoReclaim,
                DateTimeOffset.UnixEpoch,
                DateTimeOffset.UnixEpoch.AddSeconds(1),
                TimeSpan.FromSeconds(1),
                ReclaimedBytes: 0,
                LogsAvailable: true,
                ErrorMessage: null));
        using var writer = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.Yes,
            ColorSystem = ColorSystemSupport.Standard,
            Interactive = InteractionSupport.No,
            Out = new AnsiConsoleOutput(writer)
        });

        new FrameRenderer().RenderRedirected(console, frame);

        var output = writer.ToString();
        Assert.Equal(
            1,
            output.Split("Vela — WSL VHDX Compact", StringSplitOptions.None).Length - 1);
        Assert.DoesNotContain("\u001b[2J", output, StringComparison.Ordinal);
        Assert.DoesNotContain("\u001b[3J", output, StringComparison.Ordinal);
        Assert.DoesNotContain(rawPath, output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(runId.ToString(), output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(oscSecret, output, StringComparison.Ordinal);
        Assert.DoesNotContain("31m红色", output, StringComparison.Ordinal);
        Assert.Contains("完成但未回收空间", output, StringComparison.Ordinal);
        Assert.EndsWith(Environment.NewLine, output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(80)]
    [InlineData(119)]
    public void FrameRenderer_CompactBand_SeparatesNavigationAndWorkspace(int width)
    {
        var frame = new TuiFrameViewModel(
            new MainMenu().ViewModel,
            DashboardViewModel.CreateInitial(CreateProfile()),
            new RunProgressViewModel(RunProgressState.Idle, "等待操作。", null));

        var markup = new FrameRenderer().BuildMarkup(frame, width, terminalHeight: 30);

        var navigation = markup.IndexOf("操作", StringComparison.Ordinal);
        var workspace = markup.IndexOf("目标与状态", StringComparison.Ordinal);
        Assert.True(navigation >= 0);
        Assert.True(workspace > navigation);
        Assert.Contains("[bold blue]操作[/]", markup, StringComparison.Ordinal);
        Assert.Contains("[bold blue]目标与状态[/]", markup, StringComparison.Ordinal);
    }

    [Fact]
    public void FrameRenderer_MinimumBand_EmphasizesCurrentFocusWithoutFullNavigation()
    {
        var frame = new TuiFrameViewModel(
            new MainMenu().ViewModel,
            DashboardViewModel.CreateInitial(CreateProfile()),
            new RunProgressViewModel(RunProgressState.Idle, "等待操作。", null),
            selectedMenuIndex: 2);

        var markup = new FrameRenderer().BuildMarkup(frame, 79, terminalHeight: 30);

        Assert.Contains("[grey]目标[/]", markup, StringComparison.Ordinal);
        Assert.Contains("[grey]状态[/]", markup, StringComparison.Ordinal);
        Assert.Contains("[grey]焦点[/]", markup, StringComparison.Ordinal);
        Assert.Contains("[bold cyan]选择：管理目标档案[/]", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("执行压缩", markup, StringComparison.Ordinal);
    }

    [Fact]
    public void FrameRenderer_MenuSelection_EmphasizesOnlySelectedRow()
    {
        var frame = new TuiFrameViewModel(
            new MainMenu().ViewModel,
            DashboardViewModel.CreateInitial(CreateProfile()),
            new RunProgressViewModel(RunProgressState.Idle, "等待操作。", null),
            selectedMenuIndex: 1);

        var markup = new FrameRenderer().BuildMarkup(frame, 120, terminalHeight: 30);

        Assert.Contains("[bold cyan]› 执行压缩[/]", markup, StringComparison.Ordinal);
        Assert.Contains("  预检结果", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("[bold cyan]› 预检结果[/]", markup, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(RunProgressState.Running, "运行中", "cyan")]
    [InlineData(RunProgressState.Succeeded, "已完成", "green")]
    [InlineData(RunProgressState.TimedOut, "已超时", "yellow")]
    [InlineData(RunProgressState.Failed, "失败", "red")]
    public void FrameRenderer_ProgressStates_UseSemanticStyles(
        RunProgressState state,
        string label,
        string color)
    {
        var frame = new TuiFrameViewModel(
            new MainMenu().ViewModel,
            DashboardViewModel.CreateInitial(CreateProfile()),
            new RunProgressViewModel(state, "状态消息。", null));

        var markup = new FrameRenderer().BuildMarkup(frame, 120, terminalHeight: 30);

        Assert.Contains($"[bold {color}]{label}[/]", markup, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(TerminalResult.Succeeded, "成功", "green")]
    [InlineData(TerminalResult.CompletedWithNoReclaim, "完成但未回收空间", "yellow")]
    [InlineData(TerminalResult.DiskPartCompactFailed, "压缩失败", "red")]
    public void FrameRenderer_RecentDetail_DistinguishesTerminalOutcomes(
        TerminalResult result,
        string label,
        string color)
    {
        var page = new RecentRunDetailPageViewModel(
            IsMalformed: false,
            "目标档案",
            OperationIntent.Compact,
            result,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch.AddSeconds(1),
            TimeSpan.FromSeconds(1),
            ReclaimedBytes: result == TerminalResult.CompletedWithNoReclaim ? 0 : 1,
            LogsAvailable: true,
            ErrorMessage: null);
        var frame = new TuiFrameViewModel(
            new MainMenu().ViewModel,
            DashboardViewModel.CreateInitial(CreateProfile()),
            new RunProgressViewModel(RunProgressState.Succeeded, "完成。", 100),
            page: page);

        var markup = new FrameRenderer().BuildMarkup(frame, 120, terminalHeight: 30);

        Assert.Contains("运行证据", markup, StringComparison.Ordinal);
        Assert.Contains($"[bold {color}]{label}[/]", markup, StringComparison.Ordinal);
        Assert.Contains("O 打开可信日志目录", markup, StringComparison.Ordinal);
    }

    [Fact]
    public void FrameRenderer_RedirectedFrame_PreservesSemanticSectionOrder()
    {
        var frame = new TuiFrameViewModel(
            new MainMenu().ViewModel,
            DashboardViewModel.CreateInitial(CreateProfile()),
            new RunProgressViewModel(RunProgressState.Idle, "等待操作。", null));
        using var writer = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Interactive = InteractionSupport.No,
            Out = new AnsiConsoleOutput(writer)
        });

        new FrameRenderer().RenderRedirected(console, frame);

        var output = writer.ToString();
        var navigation = output.IndexOf("操作", StringComparison.Ordinal);
        var workspace = output.IndexOf("目标与状态", StringComparison.Ordinal);
        Assert.True(navigation >= 0);
        Assert.True(workspace > navigation);
        Assert.Equal(
            1,
            output.Split("Vela — WSL VHDX Compact", StringSplitOptions.None).Length - 1);
    }

    private static Profile CreateProfile() =>
        new(
            Guid.Parse("64d3e392-c081-4f1c-a95b-a7d0980527dd"),
            "Ubuntu 24.04 on D",
            "Ubuntu-24.04",
            @"D:\DevTools\WSL2\Ubuntu24.04\ext4.vhdx",
            ShutdownMode.Global,
            TimeSpan.FromSeconds(45));

}
