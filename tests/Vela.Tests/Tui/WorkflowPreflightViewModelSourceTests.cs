using Vela.Core.Contracts;
using Vela.Core.Models;
using Vela.Core.Workflows;
using Vela.Tests.Fakes;
using Vela.Tui.Application;

namespace Vela.Tests.Tui;

public sealed class WorkflowPreflightViewModelSourceTests
{
    [Fact]
    public async Task CreateAsync_MapsWorkflowEvidenceAndUsesNonEmptyPreflightRunId()
    {
        var installed = CreateInventory(WslDistributionState.Stopped);
        var running = CreateInventory(WslDistributionState.Running);
        var inspection = CreateInspection(isSparse: true);
        var journal = new FakeRunJournal();
        var wsl = new FakeWslClient
        {
            InstalledInventory = installed,
            RunningInventory = running
        };
        var resolver = new FakeLxssProfileResolver(CreateMatchedResolution());
        var inspector = new FakeVhdxInspector(inspection);
        var source = CreateSource(wsl, resolver, inspector, journal);

        var dashboard = await source.CreateAsync(CreateProfile());

        Assert.Equal("Ubuntu-24.04", dashboard.DistroName);
        Assert.True(dashboard.TargetConfigured);
        Assert.Equal(LxssResolutionStatus.Matched, dashboard.MappingState);
        Assert.Equal(TargetInspectionState.Available, dashboard.InspectionState);
        Assert.Equal(true, dashboard.VhdxEvidence?.IsSparse);
        Assert.Equal(500_000L, dashboard.VhdxEvidence?.DriveAvailableFreeSpaceBytes);
        Assert.Equal("Ubuntu-24.04", Assert.Single(dashboard.RunningDistros));
        Assert.Equal(
            installed.Distributions.Select(static distribution => distribution.Name),
            dashboard.InstalledDistros.Select(static distribution => distribution.Name));
        var runId = Assert.Single(journal.CreatedRunIds);
        Assert.NotEqual(Guid.Empty, runId);
        Assert.Equal(runId, Assert.Single(journal.CreatedRunIds));
        Assert.Equal(OperationIntent.Preflight, Assert.Single(journal.Summaries).Intent);
        Assert.Equal(TerminalResult.Succeeded, Assert.Single(journal.Summaries).TerminalResult);
        Assert.Equal(0, wsl.ShutdownAllCalls);
        Assert.Equal(0, wsl.TerminateDistroCalls);
    }

    [Fact]
    public async Task CreateAsync_MapsWarningsAndErrorsWithoutInvokingActions()
    {
        var wsl = new FakeWslClient
        {
            InstalledInventory = CreateInventory(WslDistributionState.Stopped),
            RunningInventory = CreateInventory(WslDistributionState.Running)
        };
        var resolution = new LxssProfileResolution(
            LxssResolutionStatus.Mismatched,
            "Ubuntu-24.04",
            "D:\\Registered\\Ubuntu24.04\\ext4.vhdx",
            "D:\\DevTools\\WSL2\\Ubuntu24.04\\ext4.vhdx");
        var resolver = new FakeLxssProfileResolver(resolution);
        var inspector = new FakeVhdxInspector(CreateInspection(isSparse: null));
        var source = CreateSource(wsl, resolver, inspector, new FakeRunJournal());

        var dashboard = await source.CreateAsync(CreateProfile());

        Assert.Equal("目标映射不匹配", dashboard.ErrorMessage);
        Assert.Contains(
            "稀疏状态未知",
            dashboard.Notices);
        Assert.Equal(LxssResolutionStatus.Mismatched, dashboard.MappingState);
        Assert.Equal(TargetInspectionState.Available, dashboard.InspectionState);
        Assert.Null(dashboard.VhdxEvidence?.IsSparse);
        Assert.Equal("Ubuntu-24.04", Assert.Single(dashboard.RunningDistros));
        Assert.Equal(0, wsl.ShutdownAllCalls);
        Assert.Equal(0, wsl.TerminateDistroCalls);
    }

    private static WorkflowPreflightViewModelSource CreateSource(
        FakeWslClient wsl,
        FakeLxssProfileResolver resolver,
        FakeVhdxInspector inspector,
        FakeRunJournal journal) =>
        new(new PreflightWorkflow(wsl, resolver, inspector, journal, new FixedClock()));

    private static Profile CreateProfile() => new(
        Guid.Parse("64d3e392-c081-4f1c-a95b-a7d0980527dd"),
        "Ubuntu 24.04 on D",
        "Ubuntu-24.04",
        "D:\\DevTools\\WSL2\\Ubuntu24.04\\ext4.vhdx",
        ShutdownMode.Global,
        TimeSpan.FromSeconds(45));

    private static WslInventory CreateInventory(WslDistributionState state) => new(
        DateTimeOffset.UnixEpoch,
        [new WslDistribution("Ubuntu-24.04", state, 2, true)]);

    private static LxssProfileResolution CreateMatchedResolution() => new(
        LxssResolutionStatus.Matched,
        "Ubuntu-24.04",
        "D:\\DevTools\\WSL2\\Ubuntu24.04\\ext4.vhdx",
        "D:\\DevTools\\WSL2\\Ubuntu24.04\\ext4.vhdx");

    private static VhdxInspectionResult CreateInspection(bool? isSparse) => new(
        VhdxInspectionStatus.Succeeded,
        new VhdxSnapshot(
            DateTimeOffset.UnixEpoch,
            "D:\\DevTools\\WSL2\\Ubuntu24.04\\ext4.vhdx",
            10_000L,
            DateTimeOffset.UnixEpoch,
            isSparse,
            new DriveSnapshot("D:\\", 1_000_000L, 500_000L)));
}
