using System.Collections.Immutable;
using Vela.Core.Contracts;
using Vela.Windows.Registry;

namespace Vela.Tests.Windows;

public sealed class LxssProfileResolverTests
{
    [Fact]
    public async Task ResolveAsync_NormalizesBasePathAndMatchesRequestedVhdx()
    {
        var reader = new FixtureLxssRegistryReader(
            ImmutableArray.Create(
                new LxssRegistryProfile("Ubuntu-24.04", @"D:\DevTools\WSL2\Ubuntu24.04")));
        var resolver = new LxssProfileResolver(reader);

        var result = await resolver.ResolveAsync(
            "Ubuntu-24.04",
            @"D:\DevTools\WSL2\Ubuntu24.04\.\ext4.vhdx",
            CancellationToken.None);

        Assert.Equal(LxssResolutionStatus.Matched, result.Status);
        Assert.Equal("Ubuntu-24.04", result.DistroName);
        Assert.Equal(@"D:\DevTools\WSL2\Ubuntu24.04\ext4.vhdx", result.ResolvedVhdxPath);
        Assert.Equal(@"D:\DevTools\WSL2\Ubuntu24.04\ext4.vhdx", result.NormalizedRequestedVhdxPath);
        Assert.True(result.HasStrictMatchFor("Ubuntu-24.04"));
        Assert.Equal(1, reader.ReadCalls);
    }

    [Fact]
    public async Task ResolveAsync_NormalizesExtendedLengthRegistryBasePath()
    {
        var reader = new FixtureLxssRegistryReader(
            ImmutableArray.Create(
                new LxssRegistryProfile("Ubuntu-24.04", @"\\?\D:\DevTools\WSL2\Ubuntu24.04")));
        var resolver = new LxssProfileResolver(reader);

        var result = await resolver.ResolveAsync(
            "Ubuntu-24.04",
            @"D:\DevTools\WSL2\Ubuntu24.04\ext4.vhdx",
            CancellationToken.None);

        Assert.Equal(LxssResolutionStatus.Matched, result.Status);
        Assert.Equal(@"D:\DevTools\WSL2\Ubuntu24.04\ext4.vhdx", result.ResolvedVhdxPath);
    }

    [Fact]
    public async Task ResolveAsync_WhenRequestedVhdxDiffers_ReturnsNormalizedMismatch()
    {
        var reader = new FixtureLxssRegistryReader(
            ImmutableArray.Create(
                new LxssRegistryProfile("Ubuntu-24.04", @"D:\DevTools\WSL2\Ubuntu24.04")));
        var resolver = new LxssProfileResolver(reader);

        var result = await resolver.ResolveAsync(
            "Ubuntu-24.04",
            @"D:\Other\ext4.vhdx",
            CancellationToken.None);

        Assert.Equal(LxssResolutionStatus.Mismatched, result.Status);
        Assert.Equal(@"D:\DevTools\WSL2\Ubuntu24.04\ext4.vhdx", result.ResolvedVhdxPath);
        Assert.Equal(@"D:\Other\ext4.vhdx", result.NormalizedRequestedVhdxPath);
        Assert.False(result.HasStrictPathMatch);
    }

    [Fact]
    public async Task ResolveAsync_WhenDistroIsAbsent_ReturnsNotFound()
    {
        var reader = new FixtureLxssRegistryReader(
            ImmutableArray.Create(
                new LxssRegistryProfile("Debian", @"D:\DevTools\WSL2\Debian")));
        var resolver = new LxssProfileResolver(reader);

        var result = await resolver.ResolveAsync(
            "Ubuntu-24.04",
            @"D:\DevTools\WSL2\Ubuntu24.04\ext4.vhdx",
            CancellationToken.None);

        Assert.Equal(LxssResolutionStatus.NotFound, result.Status);
        Assert.Equal("Ubuntu-24.04", result.DistroName);
        Assert.Null(result.ResolvedVhdxPath);
        Assert.Equal(@"D:\DevTools\WSL2\Ubuntu24.04\ext4.vhdx", result.NormalizedRequestedVhdxPath);
    }

    [Fact]
    public async Task ResolveAsync_UsesExactDistroNameMatching()
    {
        var reader = new FixtureLxssRegistryReader(
            ImmutableArray.Create(
                new LxssRegistryProfile("Ubuntu-24.04", @"D:\DevTools\WSL2\Ubuntu24.04")));
        var resolver = new LxssProfileResolver(reader);

        var result = await resolver.ResolveAsync(
            "ubuntu-24.04",
            @"D:\DevTools\WSL2\Ubuntu24.04\ext4.vhdx",
            CancellationToken.None);

        Assert.Equal(LxssResolutionStatus.NotFound, result.Status);
    }

    [Fact]
    public async Task ResolveAsync_WhenRegistryReaderFails_ReturnsFailedWithoutRawDiagnostic()
    {
        var reader = new FixtureLxssRegistryReader(ImmutableArray<LxssRegistryProfile>.Empty)
        {
            ThrowOnRead = true
        };
        var resolver = new LxssProfileResolver(reader);

        var result = await resolver.ResolveAsync(
            "Ubuntu-24.04",
            @"D:\DevTools\WSL2\Ubuntu24.04\ext4.vhdx",
            CancellationToken.None);

        Assert.Equal(LxssResolutionStatus.Failed, result.Status);
        Assert.Null(result.ResolvedVhdxPath);
        Assert.Equal(@"D:\DevTools\WSL2\Ubuntu24.04\ext4.vhdx", result.NormalizedRequestedVhdxPath);
    }

    [Fact]
    public async Task ResolveAsync_WhenRequestIsInvalid_DoesNotReadRegistry()
    {
        var reader = new FixtureLxssRegistryReader(
            ImmutableArray.Create(
                new LxssRegistryProfile("Ubuntu-24.04", @"D:\DevTools\WSL2\Ubuntu24.04")));
        var resolver = new LxssProfileResolver(reader);

        var result = await resolver.ResolveAsync(
            "Ubuntu-24.04",
            @"relative\ext4.vhdx",
            CancellationToken.None);

        Assert.Equal(LxssResolutionStatus.Failed, result.Status);
        Assert.Null(result.NormalizedRequestedVhdxPath);
        Assert.Equal(0, reader.ReadCalls);
    }

    [Fact]
    public async Task ResolveAsync_WhenMappedBasePathIsNotAbsolute_ReturnsFailed()
    {
        var reader = new FixtureLxssRegistryReader(
            ImmutableArray.Create(
                new LxssRegistryProfile("Ubuntu-24.04", @"relative\Ubuntu24.04")));
        var resolver = new LxssProfileResolver(reader);

        var result = await resolver.ResolveAsync(
            "Ubuntu-24.04",
            @"D:\DevTools\WSL2\Ubuntu24.04\ext4.vhdx",
            CancellationToken.None);

        Assert.Equal(LxssResolutionStatus.Failed, result.Status);
        Assert.Null(result.ResolvedVhdxPath);
    }

    [Fact]
    public async Task ResolveAsync_WhenRegistryReadIsCancelled_PropagatesCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var reader = new FixtureLxssRegistryReader(ImmutableArray<LxssRegistryProfile>.Empty)
        {
            OnRead = cancellation.Cancel,
            ThrowCancellationAfterRead = true
        };
        var resolver = new LxssProfileResolver(reader);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => resolver.ResolveAsync(
                "Ubuntu-24.04",
                @"D:\DevTools\WSL2\Ubuntu24.04\ext4.vhdx",
                cancellation.Token));
    }

    private sealed class FixtureLxssRegistryReader : ILxssRegistryReader
    {
        public FixtureLxssRegistryReader(ImmutableArray<LxssRegistryProfile> profiles)
        {
            Profiles = profiles;
        }

        public ImmutableArray<LxssRegistryProfile> Profiles { get; }

        public bool ThrowOnRead { get; init; }

        public Action? OnRead { get; init; }

        public bool ThrowCancellationAfterRead { get; init; }

        public int ReadCalls { get; private set; }

        public Task<ImmutableArray<LxssRegistryProfile>> ReadProfilesAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadCalls++;
            OnRead?.Invoke();

            if (ThrowCancellationAfterRead)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            if (ThrowOnRead)
            {
                throw new InvalidOperationException("The registry-like fixture was configured to fail.");
            }

            return Task.FromResult(Profiles);
        }
    }
}
