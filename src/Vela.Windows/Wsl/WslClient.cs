using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Vela.Core.Contracts;
using Vela.Windows.Processes;

namespace Vela.Windows.Wsl;

public sealed class WslClient : IWslClient
{
    private static readonly Regex ColumnSeparator = new(@"\s{2,}", RegexOptions.CultureInvariant);
    private readonly NativeToolPaths _nativeToolPaths;
    private readonly IProcessRunner _processRunner;

    public WslClient()
        : this(new WindowsProcessRunner(), new NativeToolPaths())
    {
    }

    public WslClient(IProcessRunner processRunner, NativeToolPaths nativeToolPaths)
    {
        ArgumentNullException.ThrowIfNull(processRunner);
        ArgumentNullException.ThrowIfNull(nativeToolPaths);
        _processRunner = processRunner;
        _nativeToolPaths = nativeToolPaths;
    }

    public async Task<WslInventory> GetInstalledInventoryAsync(CancellationToken cancellationToken)
    {
        var result = await RunInventoryAsync(
            ImmutableArray.Create("--list", "--verbose"),
            cancellationToken).ConfigureAwait(false);

        return new WslInventory(DateTimeOffset.UtcNow, ParseVerboseInventory(result.StandardOutput));
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
            CreateInvocation(arguments),
            output: null,
            cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        if (result.Status != ProcessExecutionStatus.Succeeded)
        {
            throw new InvalidOperationException("The WSL inventory command did not complete successfully.");
        }

        return result;
    }

    private ProcessInvocation CreateInvocation(ImmutableArray<string> arguments) =>
        new(_nativeToolPaths.WslExePath, arguments, Timeout: null, OutputEncoding: Encoding.Unicode);

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
