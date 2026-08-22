using System.Diagnostics;
using System.Globalization;
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

/// <summary>
/// Counts what a startup reconciliation reclaimed so the caller can tell the
/// user that leftovers from an earlier crash were cleaned up.
/// </summary>
public sealed record CompactGateReconcileResult(
    int ReclaimedGates,
    int ReclaimedPendingRequests)
{
    public static readonly CompactGateReconcileResult None = new(0, 0);

    public bool ReclaimedAnything => ReclaimedGates > 0 || ReclaimedPendingRequests > 0;
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

    // Fallback staleness bound, used only when process liveness cannot decide:
    // legacy two-field gates carry no owner, and probing another account's
    // process can fail. A compaction never legitimately runs this long.
    private static readonly TimeSpan MaxGateAge = TimeSpan.FromHours(6);

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

    private enum GateOwnerLiveness
    {
        Alive,
        Dead,
        Unknown
    }

    /// <summary>
    /// A parsed gate file. Owner fields are null for legacy two-field gates
    /// written before liveness tracking existed.
    /// </summary>
    private sealed record GateRecord(
        Guid RunId,
        string RunDirectory,
        int? OwnerProcessId,
        long? OwnerStartTicksUtc,
        long? CreatedTicksUtc);

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

        // Two attempts at most: the second one only runs after a provably stale
        // gate (and its leftovers) were reclaimed, so this cannot spin.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var pending = FindExistingPendingRun();
            if (pending is not null && pending.Value.RunId != request.RunId)
            {
                // A leftover pending request from a crashed run would otherwise
                // wedge every future compaction. Only reclaim it when the gate
                // itself proves the owner is gone; a pending request with no
                // gate still means "a run is in flight" here.
                if (attempt == 0 && TryReclaimStaleGate(pending.Value.RunId))
                {
                    continue;
                }

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
                var bytes = Utf8WithoutBom.GetBytes(BuildGateContent(request.RunId));
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
                if (attempt == 0 && TryReclaimStaleGate(additionalPendingRunId: null))
                {
                    continue;
                }

                return ReadActiveGate();
            }
            catch (UnauthorizedAccessException)
            {
                return CompactRunGateResult.Invalid(_paths.CompactGateFilePath);
            }
        }

        return ReadActiveGate();
    }

    /// <summary>
    /// Reclaims a gate whose owning process is gone, plus any pending requests
    /// left behind by it. Call at startup so leftovers from a crashed run do
    /// not surface much later as a spurious "a compaction is already running".
    /// A live gate is never touched.
    /// </summary>
    public CompactGateReconcileResult ReconcileStaleGate()
    {
        if (!_paths.IsTrustedRootDirectory() ||
            !_paths.IsTrustedPath(_paths.CompactGateFilePath))
        {
            return CompactGateReconcileResult.None;
        }

        var reclaimedGates = 0;
        if (File.Exists(_paths.CompactGateFilePath))
        {
            if (!TryReadGate(out var gate) || gate is null)
            {
                // A malformed gate is retained on purpose: TryAcquire keeps
                // reporting Invalid so the operator can inspect it.
                return CompactGateReconcileResult.None;
            }

            if (!IsGateStale(gate) || !TryDeleteGate())
            {
                return CompactGateReconcileResult.None;
            }

            reclaimedGates = 1;
        }

        // No gate is held now — the coordinator always holds one while a pending
        // request exists, so anything still in pending/ is an orphan.
        return new CompactGateReconcileResult(reclaimedGates, DeleteOrphanPendingRequests());
    }

    public void Release(Guid runId)
    {
        if (runId == Guid.Empty || !_paths.IsTrustedPath(_paths.CompactGateFilePath))
        {
            return;
        }

        try
        {
            if (!TryReadGate(out var gate) || gate is null || gate.RunId != runId)
            {
                return;
            }

            File.Delete(_paths.CompactGateFilePath);
        }
        catch (Exception)
        {
        }
    }

    private string BuildGateContent(Guid runId)
    {
        var startTicks = TryGetCurrentProcessStartTicksUtc();
        return string.Join(
            '|',
            runId.ToString("D"),
            _paths.GetRunDirectory(runId),
            Environment.ProcessId.ToString(CultureInfo.InvariantCulture),
            startTicks?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            DateTimeOffset.UtcNow.UtcTicks.ToString(CultureInfo.InvariantCulture));
    }

    private bool TryReclaimStaleGate(Guid? additionalPendingRunId)
    {
        if (!TryReadGate(out var gate) || gate is null || !IsGateStale(gate))
        {
            return false;
        }

        if (!TryDeleteGate())
        {
            return false;
        }

        TryDeletePendingRequests(gate.RunId);
        if (additionalPendingRunId is { } pendingRunId && pendingRunId != gate.RunId)
        {
            TryDeletePendingRequests(pendingRunId);
        }

        return true;
    }

    private bool IsGateStale(GateRecord gate)
    {
        if (gate.OwnerProcessId is { } processId && processId > 0)
        {
            switch (ProbeOwnerLiveness(processId, gate.OwnerStartTicksUtc))
            {
                case GateOwnerLiveness.Dead:
                    return true;
                case GateOwnerLiveness.Alive:
                    return false;
            }
        }

        return IsExpiredByAge(gate.CreatedTicksUtc);
    }

    private static GateOwnerLiveness ProbeOwnerLiveness(int processId, long? expectedStartTicksUtc)
    {
        Process process;
        try
        {
            process = Process.GetProcessById(processId);
        }
        catch (ArgumentException)
        {
            // No process with that identifier exists: the owner is gone.
            return GateOwnerLiveness.Dead;
        }
        catch (Exception)
        {
            return GateOwnerLiveness.Unknown;
        }

        using (process)
        {
            try
            {
                if (process.HasExited)
                {
                    return GateOwnerLiveness.Dead;
                }

                if (expectedStartTicksUtc is not { } expected)
                {
                    return GateOwnerLiveness.Alive;
                }

                // The identifier may have been recycled onto an unrelated
                // process; only a matching start time proves it is our owner.
                return process.StartTime.ToUniversalTime().Ticks == expected
                    ? GateOwnerLiveness.Alive
                    : GateOwnerLiveness.Dead;
            }
            catch (Exception)
            {
                return GateOwnerLiveness.Unknown;
            }
        }
    }

    private bool IsExpiredByAge(long? createdTicksUtc)
    {
        var createdAtUtc = ToCreationTimestamp(createdTicksUtc) ?? ReadGateLastWriteTimeUtc();
        if (createdAtUtc is not { } timestamp)
        {
            // Neither the gate's own stamp nor its file time is readable. Erring
            // toward "not stale" would wedge compaction forever, and no live
            // owner could have hidden its identity from the probe above.
            return true;
        }

        return DateTimeOffset.UtcNow - timestamp >= MaxGateAge;
    }

    private static DateTimeOffset? ToCreationTimestamp(long? createdTicksUtc) =>
        createdTicksUtc is { } ticks &&
        ticks >= DateTimeOffset.UnixEpoch.UtcTicks &&
        ticks <= DateTimeOffset.MaxValue.UtcTicks
            ? new DateTimeOffset(ticks, TimeSpan.Zero)
            : null;

    /// <summary>
    /// Age fallback for gates written before liveness tracking existed. Such a
    /// gate may still belong to a running worker, so it ages out rather than
    /// being reclaimed on sight.
    /// </summary>
    private DateTimeOffset? ReadGateLastWriteTimeUtc()
    {
        try
        {
            var info = new FileInfo(_paths.CompactGateFilePath);
            return info.Exists ? new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static long? TryGetCurrentProcessStartTicksUtc()
    {
        try
        {
            using var current = Process.GetCurrentProcess();
            return current.StartTime.ToUniversalTime().Ticks;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private bool TryDeleteGate()
    {
        try
        {
            File.Delete(_paths.CompactGateFilePath);
            return !File.Exists(_paths.CompactGateFilePath);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private int TryDeletePendingRequests(Guid runId)
    {
        if (runId == Guid.Empty || !_paths.IsTrustedPendingDirectory())
        {
            return 0;
        }

        var deleted = 0;
        foreach (var path in new[]
                 {
                     _paths.GetPendingRequestFilePath(runId),
                     _paths.GetPendingRequestInflightFilePath(runId)
                 })
        {
            try
            {
                if (_paths.IsTrustedPath(path) && File.Exists(path))
                {
                    File.Delete(path);
                    deleted++;
                }
            }
            catch (Exception)
            {
            }
        }

        return deleted;
    }

    private int DeleteOrphanPendingRequests()
    {
        if (!_paths.IsTrustedPendingDirectory() || !Directory.Exists(_paths.PendingDirectoryPath))
        {
            return 0;
        }

        var runIds = new List<Guid>();
        try
        {
            foreach (var path in Directory.EnumerateFiles(_paths.PendingDirectoryPath, "*.json*"))
            {
                var runText = Path.GetFileName(path).Split('.', 2)[0];
                if (Guid.TryParseExact(runText, "D", out var runId) && !runIds.Contains(runId))
                {
                    runIds.Add(runId);
                }
            }
        }
        catch (Exception)
        {
            return 0;
        }

        var deleted = 0;
        foreach (var runId in runIds)
        {
            deleted += TryDeletePendingRequests(runId);
        }

        return deleted;
    }

    private CompactRunGateResult ReadActiveGate()
    {
        if (TryReadGate(out var gate) &&
            gate is not null &&
            gate.RunId != Guid.Empty &&
            string.Equals(gate.RunDirectory, _paths.GetRunDirectory(gate.RunId), StringComparison.OrdinalIgnoreCase) &&
            _paths.IsExpectedRunDirectory(gate.RunId, gate.RunDirectory) &&
            _paths.IsTrustedRunDirectory(gate.RunId))
        {
            return CompactRunGateResult.AlreadyRunning(
                gate.RunId,
                gate.RunDirectory,
                _paths.CompactGateFilePath);
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

    private bool TryReadGate(out GateRecord? gate)
    {
        gate = null;
        try
        {
            var info = new FileInfo(_paths.CompactGateFilePath);
            if (!info.Exists || info.Length < 1 || info.Length > MaxGateBytes)
            {
                return false;
            }

            var content = File.ReadAllText(_paths.CompactGateFilePath, Utf8WithoutBom);
            var parts = content.Split('|', StringSplitOptions.None);

            // Windows paths cannot contain '|', so the field count identifies the
            // layout: five fields for gates with owner liveness, two for gates
            // written by an earlier version.
            if (parts.Length is not (2 or 5) ||
                !Guid.TryParseExact(parts[0], "D", out var runId) ||
                string.IsNullOrWhiteSpace(parts[1]))
            {
                return false;
            }

            if (parts.Length == 2)
            {
                gate = new GateRecord(runId, parts[1], null, null, null);
                return true;
            }

            gate = new GateRecord(
                runId,
                parts[1],
                TryParseInt32(parts[2]),
                TryParseInt64(parts[3]),
                TryParseInt64(parts[4]));
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static int? TryParseInt32(string text) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

    private static long? TryParseInt64(string text) =>
        long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
}
