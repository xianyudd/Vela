using System.Collections.Immutable;
using Microsoft.Win32;
using Vela.Core.Contracts;
using WindowsRegistry = Microsoft.Win32.Registry;

namespace Vela.Windows.Registry;

public sealed record LxssRegistryProfile(string DistributionName, string BasePath);

public interface ILxssRegistryReader
{
    Task<ImmutableArray<LxssRegistryProfile>> ReadProfilesAsync(
        CancellationToken cancellationToken);
}

public sealed class LxssProfileResolver : ILxssProfileResolver
{
    private readonly ILxssRegistryReader _registryReader;

    public LxssProfileResolver()
        : this(new CurrentUserLxssRegistryReader())
    {
    }

    public LxssProfileResolver(ILxssRegistryReader registryReader)
    {
        ArgumentNullException.ThrowIfNull(registryReader);
        _registryReader = registryReader;
    }

    public async Task<LxssProfileResolution> ResolveAsync(
        string distroName,
        string requestedVhdxPath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var requestedDistroName = distroName ?? string.Empty;
        var normalizedRequestedPath = NormalizeVhdxPath(requestedVhdxPath);
        if (string.IsNullOrWhiteSpace(requestedDistroName) || normalizedRequestedPath is null)
        {
            return new LxssProfileResolution(
                LxssResolutionStatus.Failed,
                requestedDistroName,
                ResolvedVhdxPath: null,
                normalizedRequestedPath);
        }

        ImmutableArray<LxssRegistryProfile> profiles;
        try
        {
            profiles = await _registryReader.ReadProfilesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return new LxssProfileResolution(
                LxssResolutionStatus.Failed,
                requestedDistroName,
                ResolvedVhdxPath: null,
                normalizedRequestedPath);
        }

        var profile = profiles.FirstOrDefault(
            candidate => string.Equals(
                candidate.DistributionName,
                requestedDistroName,
                StringComparison.Ordinal));
        if (profile is null)
        {
            return new LxssProfileResolution(
                LxssResolutionStatus.NotFound,
                requestedDistroName,
                ResolvedVhdxPath: null,
                normalizedRequestedPath);
        }

        string? resolvedPath;
        try
        {
            resolvedPath = NormalizeBasePath(profile.BasePath);
        }
        catch (Exception)
        {
            resolvedPath = null;
        }

        if (resolvedPath is null)
        {
            return new LxssProfileResolution(
                LxssResolutionStatus.Failed,
                profile.DistributionName,
                ResolvedVhdxPath: null,
                normalizedRequestedPath);
        }

        var status = string.Equals(resolvedPath, normalizedRequestedPath, StringComparison.Ordinal)
            ? LxssResolutionStatus.Matched
            : LxssResolutionStatus.Mismatched;

        return new LxssProfileResolution(
            status,
            profile.DistributionName,
            resolvedPath,
            normalizedRequestedPath);
    }

    private static string? NormalizeBasePath(string? basePath)
    {
        var normalizedBasePath = NormalizeWindowsPathPrefix(basePath);
        if (normalizedBasePath is null || !IsSafeAbsolutePath(normalizedBasePath))
        {
            return null;
        }

        return Path.GetFullPath(Path.Combine(normalizedBasePath, "ext4.vhdx"));
    }

    private static string? NormalizeVhdxPath(string? path)
    {
        var normalizedPath = NormalizeWindowsPathPrefix(path);
        if (normalizedPath is null ||
            !IsSafeAbsolutePath(normalizedPath) ||
            !normalizedPath.EndsWith(".vhdx", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return Path.GetFullPath(normalizedPath);
    }

    private static string? NormalizeWindowsPathPrefix(string? path)
    {
        if (path is null)
        {
            return null;
        }

        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
        {
            return @"\\" + path[8..];
        }

        return path.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase)
            ? path[4..]
            : path;
    }

    private static bool IsSafeAbsolutePath(string? path) =>
        !string.IsNullOrWhiteSpace(path) &&
        !path.Any(char.IsControl) &&
        Path.IsPathFullyQualified(path);
}

internal sealed class CurrentUserLxssRegistryReader : ILxssRegistryReader
{
    private const string LxssRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Lxss";

    public Task<ImmutableArray<LxssRegistryProfile>> ReadProfilesAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var profiles = ImmutableArray.CreateBuilder<LxssRegistryProfile>();
        using RegistryKey? root = WindowsRegistry.CurrentUser.OpenSubKey(LxssRegistryPath, writable: false);
        if (root is null)
        {
            return Task.FromResult(profiles.ToImmutable());
        }

        foreach (var subKeyName in root.GetSubKeyNames())
        {
            cancellationToken.ThrowIfCancellationRequested();

            using RegistryKey? subKey = root.OpenSubKey(subKeyName, writable: false);
            if (subKey is null)
            {
                continue;
            }

            if (subKey.GetValue("DistributionName") is not string distributionName ||
                subKey.GetValue("BasePath") is not string basePath)
            {
                continue;
            }

            profiles.Add(new LxssRegistryProfile(distributionName, basePath));
        }

        return Task.FromResult(profiles.ToImmutable());
    }
}
