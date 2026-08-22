using Vela.Core.Contracts;
using Vela.Core.Models;
using Vela.Tui.Application;

namespace Vela.Tests.Tui;

public sealed class CompactionTargetProfileFactoryTests
{
    [Fact]
    public void Create_uses_the_locked_distribution_name_and_vhdx_path()
    {
        var profile = CreateProfile();
        var target = new WslDistribution(
            "docker-desktop",
            WslDistributionState.Stopped,
            2,
            false,
            @"D:\Docker\wsl\data\ext4.vhdx");

        var result = CompactionTargetProfileFactory.Create(profile, target);

        Assert.NotNull(result);
        Assert.Equal(target.Name, result!.DistroName);
        Assert.Equal(target.VhdxPath, result.VhdxPath);
        Assert.Equal(profile.Id, result.Id);
        Assert.Equal(profile.DisplayName, result.DisplayName);
    }

    [Fact]
    public void Create_uses_the_profile_path_when_the_locked_target_is_the_profile_target()
    {
        var profile = CreateProfile();
        var target = new WslDistribution(
            profile.DistroName,
            WslDistributionState.Stopped,
            2,
            true);

        var result = CompactionTargetProfileFactory.Create(profile, target);

        Assert.NotNull(result);
        Assert.Equal(profile.DistroName, result!.DistroName);
        Assert.Equal(profile.VhdxPath, result.VhdxPath);
    }

    [Fact]
    public void Create_returns_null_when_a_different_locked_target_has_no_vhdx_path()
    {
        var result = CompactionTargetProfileFactory.Create(
            CreateProfile(),
            new WslDistribution(
                "docker-desktop",
                WslDistributionState.Stopped,
                2,
                false));

        Assert.Null(result);
    }

    [Fact]
    public void CreateRequest_carries_the_locked_target_into_the_compact_operation()
    {
        var runId = Guid.NewGuid();
        var target = new WslDistribution(
            "docker-desktop",
            WslDistributionState.Stopped,
            2,
            false,
            @"D:\Docker\wsl\data\ext4.vhdx");

        var request = CompactionTargetProfileFactory.CreateRequest(
            runId,
            CreateProfile(),
            target);

        Assert.NotNull(request);
        Assert.Equal(runId, request!.RunId);
        Assert.Equal(OperationIntent.Compact, request.Intent);
        Assert.Equal(target.Name, request.Profile.DistroName);
        Assert.Equal(target.VhdxPath, request.Profile.VhdxPath);
    }

    [Fact]
    public void IsTargetMismatch_flags_a_locked_row_that_addresses_another_distro()
    {
        var mismatch = CompactionTargetProfileFactory.IsTargetMismatch(
            CreateProfile(),
            new WslDistribution(
                "Ubuntu-22.04",
                WslDistributionState.Running,
                2,
                false,
                @"D:\Other\ext4.vhdx"));

        // The stored profile's shutdown scope and "safe" labelling were written
        // for its own distro, so addressing another one must be surfaced.
        Assert.True(mismatch);
    }

    [Fact]
    public void IsTargetMismatch_ignores_casing_differences()
    {
        var profile = CreateProfile();

        var mismatch = CompactionTargetProfileFactory.IsTargetMismatch(
            profile,
            new WslDistribution(
                profile.DistroName.ToLowerInvariant(),
                WslDistributionState.Running,
                2,
                true,
                profile.VhdxPath));

        Assert.False(mismatch);
    }

    [Fact]
    public void IsTargetMismatch_reports_no_mismatch_without_a_locked_target()
    {
        Assert.False(CompactionTargetProfileFactory.IsTargetMismatch(CreateProfile(), lockedTarget: null));
    }

    [Fact]
    public void IsTargetMismatch_reports_no_mismatch_for_a_blank_locked_name()
    {
        // A blank row is rejected by Create as well; it must not raise a warning
        // that names an empty distro.
        Assert.False(CompactionTargetProfileFactory.IsTargetMismatch(
            CreateProfile(),
            new WslDistribution("   ", WslDistributionState.Stopped, 2, false)));
    }

    private static Profile CreateProfile() => new(
        Guid.Parse("ed979041-296f-49fd-9aae-61ceacbb06c0"),
        "Ubuntu 24.04",
        "Ubuntu-24.04",
        @"D:\Vela\ext4.vhdx",
        ShutdownMode.Global,
        TimeSpan.FromSeconds(45));
}
