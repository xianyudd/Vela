using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Vela.Core.Contracts;
using Vela.Core.Models;
using Vela.Core.Validation;
using Vela.Windows.Diagnostics;

namespace Vela.Windows.Elevation;

public sealed class OperationRequestStore : IOperationRequestStore
{
    public const long MaxRequestBytes = 64 * 1024;

    private static readonly Encoding Utf8WithoutBom =
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly AppPaths _paths;

    public OperationRequestStore(AppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _paths = paths;
    }

    public async Task<OperationRequestWriteResult> WriteAsync(
        OperationRequest request,
        CancellationToken cancellationToken)
    {
        if (!IsWritableRequest(request))
        {
            return OperationRequestWriteResult.Failure();
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_paths.IsTrustedRootDirectory() || !_paths.IsTrustedPendingDirectory())
            {
                return OperationRequestWriteResult.Failure();
            }

            var requestPath = _paths.GetPendingRequestFilePath(request.RunId);
            var temporaryPath = _paths.GetPendingRequestTemporaryFilePath(request.RunId);
            var inflightPath = _paths.GetPendingRequestInflightFilePath(request.RunId);
            var eventsPath = _paths.GetEventsFilePath(request.RunId);

            if (!_paths.IsTrustedPath(requestPath) ||
                !_paths.IsTrustedPath(temporaryPath) ||
                !_paths.IsTrustedPath(inflightPath) ||
                !_paths.IsTrustedPath(eventsPath) ||
                !File.Exists(eventsPath) ||
                File.Exists(inflightPath))
            {
                return OperationRequestWriteResult.Failure();
            }

            Directory.CreateDirectory(_paths.PendingDirectoryPath);
            if (!_paths.IsTrustedPendingDirectory())
            {
                return OperationRequestWriteResult.Failure();
            }

            try
            {
                await WriteJsonAsync(temporaryPath, request, cancellationToken).ConfigureAwait(false);
                if (new FileInfo(temporaryPath).Length > MaxRequestBytes)
                {
                    return OperationRequestWriteResult.Failure();
                }

                ReplaceAtomically(temporaryPath, requestPath);
            }
            finally
            {
                TryDelete(temporaryPath);
            }

            return OperationRequestWriteResult.Success(requestPath);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return OperationRequestWriteResult.Failure();
        }
    }

    public async Task<OperationRequestReadResult> ReadAsync(
        Guid expectedRunId,
        CancellationToken cancellationToken)
    {
        if (expectedRunId == Guid.Empty)
        {
            return OperationRequestReadResult.Failure();
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var requestPath = _paths.GetPendingRequestFilePath(expectedRunId);
            if (!_paths.IsExpectedPendingRequestPath(expectedRunId, requestPath) ||
                !_paths.IsTrustedPath(requestPath) ||
                !TryGetRequestFileWithinLimit(requestPath))
            {
                return OperationRequestReadResult.Failure();
            }

            var request = await ReadJsonAsync(requestPath, cancellationToken).ConfigureAwait(false);
            return request is null
                ? OperationRequestReadResult.Failure()
                : OperationRequestReadResult.Success(request, requestPath);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return OperationRequestReadResult.Failure();
        }
    }

    public async Task<OperationRequestClaimResult> ClaimAsync(
        Guid expectedRunId,
        CancellationToken cancellationToken)
    {
        if (expectedRunId == Guid.Empty)
        {
            return OperationRequestClaimResult.Failure();
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_paths.IsTrustedRootDirectory() || !_paths.IsTrustedPendingDirectory())
            {
                return OperationRequestClaimResult.Failure();
            }

            var requestPath = _paths.GetPendingRequestFilePath(expectedRunId);
            var inflightPath = _paths.GetPendingRequestInflightFilePath(expectedRunId);
            if (!_paths.IsExpectedPendingRequestPath(expectedRunId, requestPath) ||
                !_paths.IsExpectedPendingRequestInflightPath(expectedRunId, inflightPath) ||
                !_paths.IsTrustedPath(requestPath) ||
                !_paths.IsTrustedPath(inflightPath) ||
                File.Exists(inflightPath) ||
                !TryGetRequestFileWithinLimit(requestPath))
            {
                return OperationRequestClaimResult.Failure();
            }

            File.Move(requestPath, inflightPath);
            if (!File.Exists(inflightPath) || !_paths.IsTrustedPath(inflightPath))
            {
                return OperationRequestClaimResult.Failure();
            }

            var request = await ReadJsonAsync(inflightPath, cancellationToken).ConfigureAwait(false);
            return request is null || request.RunId != expectedRunId
                ? OperationRequestClaimResult.Failure()
                : OperationRequestClaimResult.Success(request, inflightPath);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return OperationRequestClaimResult.Failure();
        }
    }

    public Task<OperationRequestConsumeResult> ConsumeAsync(
        Guid expectedRunId,
        CancellationToken cancellationToken)
    {
        if (expectedRunId == Guid.Empty)
        {
            return Task.FromResult(OperationRequestConsumeResult.Failure());
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var requestPath = _paths.GetPendingRequestFilePath(expectedRunId);
            var inflightPath = _paths.GetPendingRequestInflightFilePath(expectedRunId);
            if (!_paths.IsExpectedPendingRequestPath(expectedRunId, requestPath) ||
                !_paths.IsExpectedPendingRequestInflightPath(expectedRunId, inflightPath) ||
                !_paths.IsTrustedPath(requestPath) ||
                !_paths.IsTrustedPath(inflightPath))
            {
                return Task.FromResult(OperationRequestConsumeResult.Failure());
            }

            var consumed = false;
            if (File.Exists(inflightPath))
            {
                File.Delete(inflightPath);
                consumed = true;
            }

            if (File.Exists(requestPath))
            {
                File.Delete(requestPath);
                consumed = true;
            }

            return Task.FromResult(
                consumed
                    ? OperationRequestConsumeResult.Success()
                    : OperationRequestConsumeResult.Failure());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return Task.FromResult(OperationRequestConsumeResult.Failure());
        }
    }

    private static bool IsWritableRequest(OperationRequest? request) =>
        request is not null &&
        request.RunId != Guid.Empty &&
        request.Intent == OperationIntent.Compact &&
        request.Profile is not null &&
        ProfileValidator.Validate(request.Profile).IsValid;

    private static bool TryGetRequestFileWithinLimit(string path)
    {
        try
        {
            return File.Exists(path) && new FileInfo(path).Length <= MaxRequestBytes;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static async Task<OperationRequest?> ReadJsonAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            useAsync: true);
        return await JsonSerializer.DeserializeAsync<OperationRequest>(
                stream,
                SerializerOptions,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task WriteJsonAsync(
        string path,
        OperationRequest request,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4096,
            useAsync: true);
        await JsonSerializer.SerializeAsync(stream, request, SerializerOptions, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
    }

    private static void ReplaceAtomically(string temporaryPath, string destinationPath)
    {
        if (File.Exists(destinationPath))
        {
            File.Replace(temporaryPath, destinationPath, destinationBackupFileName: null);
            return;
        }

        File.Move(temporaryPath, destinationPath);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception)
        {
        }
    }
}
