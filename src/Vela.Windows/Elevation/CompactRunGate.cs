using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Vela.Core.Models;
using Vela.Core.Validation;
using Vela.Windows.Diagnostics;

namespace Vela.Windows.Elevation;

public enum CompactRunGateStatus
{
    Acquired,
    AlreadyRunning,
    Invalid
}

public sealed record CompactRunGateResult(
    CompactRunGateStatus Status,
    Guid? ActiveRunId,
    string? RunDirectory,
    string? GatePath,
    CompactRunGateLease? Lease)
{
    public static CompactRunGateResult Invalid(string gatePath) =>
        new(CompactRunGateStatus.Invalid, null, null, gatePath, null);

    public static CompactRunGateResult AlreadyRunning(
        Guid runId,
        string runDirectory,
        string gatePath) =>
        new(CompactRunGateStatus.AlreadyRunning, runId, runDirectory, gatePath, null);

    public static CompactRunGateResult Acquired(
        Guid runId,
        string runDirectory,
        string gatePath,
        CompactRunGateLease lease) =>
        new(CompactRunGateStatus.Acquired, runId, runDirectory, gatePath, lease);
}

public sealed class CompactRunGateLease : IDisposable
{
    private CompactRunGate? _gate;

    internal CompactRunGateLease(CompactRunGate gate, Guid runId)
    {
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        RunId = runId == Guid.Empty
            ? throw new ArgumentException("A non-empty run identifier is required.", nameof(runId))
            : runId;
    }

    public Guid RunId { get; }

    public void Dispose() => Interlocked.Exchange(ref _gate, null)?.Release(RunId);
}

public sealed class CompactRunGate
{
    private const long MaxGateBytes = 4096;
    private const long MaxPendingBytes = 64 * 1024;
    private static readonly Encoding Utf8WithoutBom = new UTF8Encoding(false);
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };
    private readonly AppPaths _paths;

    public CompactRunGate(AppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _paths = paths;
    }

    public CompactRunGateResult TryAcquire(OperationRequest request)
    {
        if (request is null ||
            request.RunId == Guid.Empty ||
            request.Intent != OperationIntent.Compact ||
            request.Profile is null ||
            !ProfileValidator.Validate(request.Profile).IsValid ||
            !_paths.IsTrustedRootDirectory() ||
            !_paths.IsTrustedPath(_paths.CompactGateFilePath))
        {
            return CompactRunGateResult.Invalid(_paths.CompactGateFilePath);
        }

        try
        {
            Directory.CreateDirectory(_paths.RootDirectory);
        }
        catch (Exception)
        {
            return CompactRunGateResult.Invalid(_paths.CompactGateFilePath);
        }

        var pending = FindExistingPendingRun();
        if (pending is not null && pending.Value.RunId != request.RunId)
        {
            return CompactRunGateResult.AlreadyRunning(
                pending.Value.RunId,
                _paths.GetRunDirectory(pending.Value.RunId),
                pending.Value.Path);
        }

        try
        {
            using var stream = new FileStream(
                _paths.CompactGateFilePath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.Read,
                4096,
                FileOptions.WriteThrough);
            var content = $"{request.RunId:D}|{_paths.GetRunDirectory(request.RunId)}";
            var bytes = Utf8WithoutBom.GetBytes(content);
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush(flushToDisk: true);
            return CompactRunGateResult.Acquired(
                request.RunId,
                _paths.GetRunDirectory(request.RunId),
                _paths.CompactGateFilePath,
                new CompactRunGateLease(this, request.RunId));
        }
        catch (IOException)
        {
            var active = ReadActiveGate();
            return active;
        }
        catch (UnauthorizedAccessException)
        {
            return CompactRunGateResult.Invalid(_paths.CompactGateFilePath);
        }
    }

    public void Release(Guid runId)
    {
        if (runId == Guid.Empty || !_paths.IsTrustedPath(_paths.CompactGateFilePath))
        {
            return;
        }

        try
        {
            if (!TryReadGate(out var activeRunId, out _) || activeRunId != runId)
            {
                return;
            }

            File.Delete(_paths.CompactGateFilePath);
        }
        catch (Exception)
        {
        }
    }

    private CompactRunGateResult ReadActiveGate()
    {
        if (TryReadGate(out var runId, out var runDirectory) &&
            runId != Guid.Empty &&
            runDirectory is not null &&
            string.Equals(runDirectory, _paths.GetRunDirectory(runId), StringComparison.OrdinalIgnoreCase) &&
            _paths.IsExpectedRunDirectory(runId, runDirectory) &&
            _paths.IsTrustedRunDirectory(runId))
        {
            return CompactRunGateResult.AlreadyRunning(runId, runDirectory, _paths.CompactGateFilePath);
        }

        return CompactRunGateResult.Invalid(_paths.CompactGateFilePath);
    }

    private (Guid RunId, string Path)? FindExistingPendingRun()
    {
        try
        {
            if (!_paths.IsTrustedPendingDirectory() || !Directory.Exists(_paths.PendingDirectoryPath))
            {
                return null;
            }

            foreach (var path in Directory.EnumerateFiles(_paths.PendingDirectoryPath, "*.json*"))
            {
                var fileName = Path.GetFileName(path);
                var runText = fileName.Split('.', 2)[0];
                if (!Guid.TryParseExact(runText, "D", out var runId) ||
                    !_paths.IsTrustedPath(path) ||
                    (!string.Equals(path, _paths.GetPendingRequestFilePath(runId), StringComparison.OrdinalIgnoreCase) &&
                     !string.Equals(path, _paths.GetPendingRequestInflightFilePath(runId), StringComparison.OrdinalIgnoreCase)) ||
                    !TryReadValidPendingRequest(path, runId, out var request))
                {
                    continue;
                }

                return (request!.RunId, path);
            }
        }
        catch (Exception)
        {
        }

        return null;
    }

    private bool TryReadValidPendingRequest(
        string path,
        Guid expectedRunId,
        out OperationRequest? request)
    {
        request = null;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length < 1 || info.Length > MaxPendingBytes)
            {
                return false;
            }

            request = JsonSerializer.Deserialize<OperationRequest>(
                File.ReadAllText(path, Utf8WithoutBom),
                SerializerOptions);
            return request is not null &&
                   request.RunId == expectedRunId &&
                   request.Intent == OperationIntent.Compact &&
                   request.Profile is not null &&
                   ProfileValidator.Validate(request.Profile).IsValid;
        }
        catch (Exception)
        {
            request = null;
            return false;
        }
    }

    private bool TryReadGate(out Guid runId, out string? runDirectory)
    {
        runId = Guid.Empty;
        runDirectory = null;
        try
        {
            var info = new FileInfo(_paths.CompactGateFilePath);
            if (!info.Exists || info.Length < 1 || info.Length > MaxGateBytes)
            {
                return false;
            }

            var content = File.ReadAllText(_paths.CompactGateFilePath, Utf8WithoutBom);
            var parts = content.Split('|', 2, StringSplitOptions.None);
            return parts.Length == 2 &&
                   Guid.TryParseExact(parts[0], "D", out runId) &&
                   !string.IsNullOrWhiteSpace(runDirectory = parts[1]);
        }
        catch (Exception)
        {
            return false;
        }
    }
}
