using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Vela.Core.Contracts;
using Vela.Windows.Processes;
using Vela.Windows.Registry;

namespace Vela.Windows.Wsl;

public sealed class WslClient : IWslClient
{
    private static readonly Regex ColumnSeparator = new(@"\s{2,}", RegexOptions.CultureInvariant);

    // Read-only inventory queries must not hang the UI: wsl.exe can stall while
    // the utility VM starts or a distro is wedged. Destructive commands keep
    // Timeout: null because their deadline is the profile's shutdown timeout.
    private static readonly TimeSpan InventoryTimeout = TimeSpan.FromSeconds(30);
    private readonly NativeToolPaths _nativeToolPaths;
    private readonly IProcessRunner _processRunner;
    private readonly ILxssRegistryReader _registryReader;

    public WslClient()
        : this(
            new WindowsProcessRunner(),
            new NativeToolPaths(),
            new CurrentUserLxssRegistryReader())
    {
    }

    public WslClient(IProcessRunner processRunner, NativeToolPaths nativeToolPaths)
        : this(processRunner, nativeToolPaths, new CurrentUserLxssRegistryReader())
    {
    }

    public WslClient(
        IProcessRunner processRunner,
        NativeToolPaths nativeToolPaths,
        ILxssRegistryReader registryReader)
    {
        ArgumentNullException.ThrowIfNull(processRunner);
        ArgumentNullException.ThrowIfNull(nativeToolPaths);
        ArgumentNullException.ThrowIfNull(registryReader);
        _processRunner = processRunner;
        _nativeToolPaths = nativeToolPaths;
        _registryReader = registryReader;
    }

    public async Task<WslInventory> GetInstalledInventoryAsync(CancellationToken cancellationToken)
    {
        var result = await RunInventoryAsync(
            ImmutableArray.Create("--list", "--verbose"),
            cancellationToken).ConfigureAwait(false);

        var distributions = ParseVerboseInventory(result.StandardOutput);
        var profiles = await ReadRegistryProfilesAsync(cancellationToken).ConfigureAwait(false);
        return new WslInventory(
            DateTimeOffset.UtcNow,
            EnrichStorageEvidence(distributions, profiles));
    }

    public async Task<WslInventory> GetRunningInventoryAsync(CancellationToken cancellationToken)
    {
        var result = await RunInventoryAsync(
            ImmutableArray.Create("--list", "--running", "--quiet"),
            cancellationToken).ConfigureAwait(false);

        return new WslInventory(DateTimeOffset.UtcNow, ParseRunningInventory(result.StandardOutput));
    }

    public Task<ProcessExecutionResult> ShutdownAllAsync(CancellationToken cancellationToken) =>
        _processRunner.RunAsync(
            CreateInvocation(ImmutableArray.Create("--shutdown")),
            output: null,
            cancellationToken);

    public Task<ProcessExecutionResult> TerminateDistroAsync(
        string distroName,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(distroName) || distroName.Any(char.IsControl))
        {
            return Task.FromResult(CreateRejectedResult());
        }

        return _processRunner.RunAsync(
            CreateInvocation(ImmutableArray.Create("--terminate", distroName)),
            output: null,
            cancellationToken);
    }

    private async Task<ProcessExecutionResult> RunInventoryAsync(
        ImmutableArray<string> arguments,
        CancellationToken cancellationToken)
    {
        var result = await _processRunner.RunAsync(
            CreateInvocation(arguments, InventoryTimeout),
            output: null,
            cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        if (result.Status != ProcessExecutionStatus.Succeeded)
        {
            throw new InvalidOperationException("The WSL inventory command did not complete successfully.");
        }

        return result;
    }

    private ProcessInvocation CreateInvocation(
        ImmutableArray<string> arguments,
        TimeSpan? timeout = null) =>
        new(_nativeToolPaths.WslExePath, arguments, timeout, OutputEncoding: Encoding.Unicode);

    private async Task<ImmutableArray<LxssRegistryProfile>> ReadRegistryProfilesAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            return await _registryReader
                .ReadProfilesAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // WSL inventory remains useful when the optional registry evidence
            // read is unavailable. The UI will render the missing evidence as
            // a bounded, actionable state instead of inventing a value.
            return ImmutableArray<LxssRegistryProfile>.Empty;
        }
    }

    private static ImmutableArray<WslDistribution> EnrichStorageEvidence(
        ImmutableArray<WslDistribution> distributions,
        ImmutableArray<LxssRegistryProfile> profiles)
    {
        if (distributions.IsDefaultOrEmpty || profiles.IsDefaultOrEmpty)
        {
            return distributions;
        }

        return distributions
            .Select(distribution =>
            {
                var profile = profiles.FirstOrDefault(candidate =>
                    string.Equals(
                        candidate.DistributionName,
                        distribution.Name,
                        StringComparison.OrdinalIgnoreCase));
                var vhdxPath = ResolveVhdxPath(profile?.BasePath);
                return distribution with
                {
                    VhdxPath = vhdxPath,
                    VhdxSizeBytes = ReadVhdxSize(vhdxPath)
                };
            })
            .ToImmutableArray();
    }

    private static string? ResolveVhdxPath(string? basePath)
    {
        var normalizedBasePath = NormalizeWindowsPathPrefix(basePath);
        if (normalizedBasePath is null ||
            normalizedBasePath.Any(char.IsControl) ||
            !Path.IsPathFullyQualified(normalizedBasePath))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(Path.Combine(normalizedBasePath, "ext4.vhdx"));
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static long? ReadVhdxSize(string? vhdxPath)
    {
        if (string.IsNullOrWhiteSpace(vhdxPath))
        {
            return null;
        }

        try
        {
            var file = new FileInfo(vhdxPath);
            return file.Exists ? file.Length : null;
        }
        catch (Exception)
        {
            return null;
        }
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

    private static ImmutableArray<WslDistribution> ParseVerboseInventory(
        ImmutableArray<string> output)
    {
        var distributions = ImmutableArray.CreateBuilder<WslDistribution>();

        foreach (var line in output)
        {
            var distribution = ParseVerboseLine(line);
            if (distribution is not null)
            {
                distributions.Add(distribution);
            }
        }

        return distributions.ToImmutable();
    }

    private static WslDistribution? ParseVerboseLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        var value = NormalizeRedirectedLine(line).Trim().TrimStart('\uFEFF');
        var isDefault = value.StartsWith('*');
        if (isDefault)
        {
            value = value[1..].TrimStart();
        }

        var columns = ColumnSeparator.Split(value);
        if (columns.Length < 3 ||
            !int.TryParse(
                columns[^1],
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var version))
        {
            return null;
        }

        var name = string.Join("  ", columns.Take(columns.Length - 2)).Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        return new WslDistribution(
            name,
            ParseState(columns[^2]),
            version,
            isDefault);
    }

    private static ImmutableArray<WslDistribution> ParseRunningInventory(
        ImmutableArray<string> output)
    {
        var distributions = ImmutableArray.CreateBuilder<WslDistribution>();

        foreach (var line in output)
        {
            var name = NormalizeRedirectedLine(line).Trim().TrimStart('\uFEFF');
            if (!string.IsNullOrWhiteSpace(name))
            {
                distributions.Add(new WslDistribution(
                    name,
                    WslDistributionState.Running,
                    Version: null,
                    IsDefault: false));
            }
        }

        return distributions.ToImmutable();
    }

    private static string NormalizeRedirectedLine(string line) =>
        line.IndexOf('\0', StringComparison.Ordinal) >= 0
            ? line.Replace("\0", string.Empty, StringComparison.Ordinal)
            : line;

    private static WslDistributionState ParseState(string state)
    {
        var normalizedState = state.Trim();
        if (string.Equals(normalizedState, "Running", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalizedState, "正在运行", StringComparison.Ordinal) ||
            string.Equals(normalizedState, "运行中", StringComparison.Ordinal))
        {
            return WslDistributionState.Running;
        }

        if (string.Equals(normalizedState, "Stopped", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalizedState, "已停止", StringComparison.Ordinal) ||
            string.Equals(normalizedState, "停止", StringComparison.Ordinal))
        {
            return WslDistributionState.Stopped;
        }

        return WslDistributionState.Unknown;
    }

    private static ProcessExecutionResult CreateRejectedResult()
    {
        var occurredAtUtc = DateTimeOffset.UtcNow;

        return new ProcessExecutionResult(
            ProcessExecutionStatus.LaunchFailed,
            ExitCode: null,
            ImmutableArray<string>.Empty,
            ImmutableArray<string>.Empty,
            occurredAtUtc,
            occurredAtUtc);
    }
}
