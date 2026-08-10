using Vela.Core.Contracts;
using Vela.Windows.Processes;
using Vela.Windows.Storage;

namespace Vela.Tests.Windows;

public sealed class WslCompactionImpactEstimatorTests
{
    [Fact]
    public async Task EstimateAsync_uses_guest_used_bytes_to_calculate_reclaimable_space()
    {
        var runner = new Fakes.FakeProcessRunner
        {
            ThrowOnInvocation = false,
            Result = CreateResult(
                "Filesystem  1B-blocks  Used  Available  Use% Mounted on",
                "/dev/sdc    10737418240 4294967296 6442450944 40% /")
        };
        var estimator = new WslCompactionImpactEstimator(runner, new NativeToolPaths());

        var result = await estimator.EstimateAsync(
            "docker-desktop",
            currentVhdxSizeBytes: 10L * 1024 * 1024 * 1024,
            CancellationToken.None);

        Assert.Equal(CompactionImpactStatus.Estimated, result.Status);
        Assert.Equal(4L * 1024 * 1024 * 1024, result.UsedBytes);
        Assert.Equal(6L * 1024 * 1024 * 1024, result.ReclaimableBytes);
        Assert.Equal(
            new[] { "--distribution", "docker-desktop", "--", "df", "-B1", "-P", "/" },
            Assert.Single(runner.Invocations).Arguments);
    }

    [Fact]
    public async Task EstimateAsync_returns_unavailable_when_df_output_has_no_used_bytes()
    {
        var runner = new Fakes.FakeProcessRunner
        {
            ThrowOnInvocation = false,
            Result = CreateResult("df: unavailable")
        };
        var estimator = new WslCompactionImpactEstimator(runner, new NativeToolPaths());

        var result = await estimator.EstimateAsync(
            "Ubuntu-24.04",
            currentVhdxSizeBytes: 10L * 1024 * 1024 * 1024,
            CancellationToken.None);

        Assert.Equal(CompactionImpactStatus.Unavailable, result.Status);
        Assert.Null(result.ReclaimableBytes);
    }

    private static ProcessExecutionResult CreateResult(params string[] output) =>
        new(
            ProcessExecutionStatus.Succeeded,
            0,
            output.ToImmutableArray(),
            ImmutableArray<string>.Empty,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch);
}
