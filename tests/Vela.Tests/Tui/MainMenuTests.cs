using System.Collections.Immutable;
using Vela.Core.Contracts;
using Vela.Core.Models;
using Vela.Tui.Menu;
using Vela.Tui.Rendering;
using Vela.Tui.Screens;

namespace Vela.Tests.Tui;

public sealed class MainMenuTests
{
    [Fact]
    public void ViewModel_ContainsTheSixExpectedMenuLabels()
    {
        var console = new RecordingConsole();
        var input = new RecordingMenuInput(MainMenuAction.Preflight);
        var menu = new MainMenu(console, input);

        Assert.Equal(
            new[]
            {
                "预检（只读）",
                "执行压缩",
                "管理目标档案",
                "查看最近运行记录",
                "打开日志目录",
                "退出"
            },
            menu.ViewModel.Items.Select(static item => item.Label));
        Assert.Equal("Vela — WSL VHDX Compact", menu.ViewModel.Title);
    }

    [Fact]
    public void Prompt_UsesInjectedInputAndConsoleAdapter()
    {
        var console = new RecordingConsole();
        var input = new RecordingMenuInput(MainMenuAction.RecentRuns);
        var menu = new MainMenu(console, input);

        var action = menu.Prompt();

        Assert.Equal(MainMenuAction.RecentRuns, action);
        Assert.Same(menu.ViewModel, input.LastViewModel);
        Assert.Same(menu.ViewModel, console.LastMenuViewModel);
    }

    [Fact]
    public void CreateExecuteConfirmation_RequiresExactYesAndShowsRunningDistros()
    {
        var profile = CreateProfile();
        var confirmation = MainMenu.CreateExecuteConfirmation(
            profile,
            ImmutableArray.Create(
                new WslDistribution("Ubuntu-24.04", WslDistributionState.Running, 2, true),
                new WslDistribution("docker-desktop", WslDistributionState.Running, 2, false)));

        Assert.Equal("YES", confirmation.RequiredInput);
        Assert.Contains("YES", confirmation.Prompt, StringComparison.Ordinal);
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

        Assert.Contains(profile.ShutdownMode.ToString(), confirmation.Prompt, StringComparison.Ordinal);
        Assert.Contains(profile.VhdxPath, confirmation.Prompt, StringComparison.Ordinal);
        Assert.Contains(dataRootDirectory, confirmation.Prompt, StringComparison.Ordinal);
        Assert.Contains("影响", confirmation.Prompt, StringComparison.Ordinal);
        Assert.Contains("Ubuntu-24.04", confirmation.Prompt, StringComparison.Ordinal);
        Assert.Equal("YES", confirmation.RequiredInput);
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

    [Fact]
    public void DashboardAndProgress_RenderTheProvidedImmutableViewModels()
    {
        var console = new RecordingConsole();
        var dashboard = new DashboardScreen(console);
        var renderer = new RunRenderer(console);
        var dashboardViewModel = DashboardViewModel.CreateInitial(CreateProfile()) with
        {
            Notices = ImmutableArray.Create("Sparse state is unknown."),
            ErrorMessage = "Registry mapping does not match the requested VHDX.",
            RunDirectory = @"D:\Logs\00000000-0000-0000-0000-000000000001"
        };
        var progressViewModel = new RunProgressViewModel(
            RunProgressState.Preflighting,
            "正在采集只读预检证据。",
            Percent: 40);

        dashboard.Render(dashboardViewModel);
        renderer.Render(progressViewModel);

        Assert.Equal("Profile: Ubuntu 24.04 on D", dashboardViewModel.ProfileTitle);
        Assert.Equal("Registry mapping does not match the requested VHDX.", console.LastDashboardViewModel?.ErrorMessage);
        Assert.Equal(ImmutableArray.Create("Sparse state is unknown."), console.LastDashboardViewModel?.Notices);
        Assert.Same(progressViewModel, console.LastProgressViewModel);
        Assert.Equal(RunProgressState.Preflighting, console.LastProgressViewModel?.State);
    }

    [Fact]
    public void PreflightScreen_RendersTheInjectedReadOnlyPreview()
    {
        var profile = CreateProfile();
        var console = new RecordingConsole();
        var dashboard = new DashboardScreen(console);
        var renderer = new RunRenderer(console);
        var preview = DashboardViewModel.CreateInitial(profile) with
        {
            RegistryMapping = profile.VhdxPath,
            Notices = ImmutableArray.Create("Fake preflight preview."),
            RunDirectory = @"D:\Artifacts\fake-preflight"
        };
        var source = new RecordingPreflightViewModelSource(preview);
        var screen = new PreflightScreen(dashboard, renderer, source);

        screen.Render(profile);

        Assert.Same(profile, source.LastProfile);
        Assert.Same(preview, console.LastDashboardViewModel);
        Assert.Equal(
            new[] { RunProgressState.Preflighting, RunProgressState.Succeeded },
            console.ProgressViewModels.Select(static progress => progress.State));
    }

    private static Profile CreateProfile() =>
        new(
            Guid.Parse("64d3e392-c081-4f1c-a95b-a7d0980527dd"),
            "Ubuntu 24.04 on D",
            "Ubuntu-24.04",
            @"D:\DevTools\WSL2\Ubuntu24.04\ext4.vhdx",
            ShutdownMode.Global,
            TimeSpan.FromSeconds(45));

    private sealed class RecordingConsole : IVelaConsole
    {
        public MainMenuViewModel? LastMenuViewModel { get; private set; }

        public DashboardViewModel? LastDashboardViewModel { get; private set; }

        public RunProgressViewModel? LastProgressViewModel { get; private set; }

        public ImmutableArray<RunProgressViewModel> ProgressViewModels { get; private set; } =
            ImmutableArray<RunProgressViewModel>.Empty;

        public void RenderDashboard(DashboardViewModel viewModel) => LastDashboardViewModel = viewModel;

        public void RenderMenu(MainMenuViewModel viewModel) => LastMenuViewModel = viewModel;

        public void RenderProgress(RunProgressViewModel viewModel)
        {
            LastProgressViewModel = viewModel;
            ProgressViewModels = ProgressViewModels.Add(viewModel);
        }
    }

    private sealed class RecordingMenuInput : IMenuInput
    {
        private readonly MainMenuAction _selection;

        public RecordingMenuInput(MainMenuAction selection)
        {
            _selection = selection;
        }

        public MainMenuViewModel? LastViewModel { get; private set; }

        public MainMenuAction Select(MainMenuViewModel viewModel)
        {
            LastViewModel = viewModel;
            return _selection;
        }
    }

    private sealed class RecordingPreflightViewModelSource : IPreflightViewModelSource
    {
        private readonly DashboardViewModel _preview;

        public RecordingPreflightViewModelSource(DashboardViewModel preview)
        {
            _preview = preview;
        }

        public Profile? LastProfile { get; private set; }

        public DashboardViewModel Create(Profile profile)
        {
            LastProfile = profile;
            return _preview;
        }
    }
}
