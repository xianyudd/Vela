using System.Collections.Immutable;
using Vela.Windows.Configuration;
using Vela.Windows.Diagnostics;

namespace Vela.Tui.Application;

public enum StartupGateStatus
{
    Ready,
    Initialized,
    Failed
}

public sealed record StartupInspection(
    bool RootDirectoryExists,
    bool ConfigurationFileExists,
    bool PendingDirectoryExists,
    bool LogsDirectoryExists,
    bool RootDirectoryIsDirectory = false,
    bool ConfigurationPathIsFile = false,
    bool PendingPathIsDirectory = false,
    bool LogsPathIsDirectory = false,
    bool PathsTrusted = false)
{
    public bool IsComplete =>
        RootDirectoryExists &&
        ConfigurationFileExists &&
        PendingDirectoryExists &&
        LogsDirectoryExists &&
        RootDirectoryIsDirectory &&
        ConfigurationPathIsFile &&
        PendingPathIsDirectory &&
        LogsPathIsDirectory &&
        PathsTrusted;
}

public sealed record StartupGateResult(
    StartupGateStatus Status,
    StartupInspection Inspection,
    string Message)
{
    public bool IsReady =>
        Status is StartupGateStatus.Ready or StartupGateStatus.Initialized;
}

public sealed class StartupGate
{
    private readonly AppPaths _paths;
    private readonly JsonProfileStore _profileStore;

    public StartupGate(AppPaths paths, JsonProfileStore profileStore)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(profileStore);

        _paths = paths;
        _profileStore = profileStore;
    }

    public StartupInspection Inspect()
    {
        var rootExists = Directory.Exists(_paths.RootDirectory);
        var configurationExists = File.Exists(_paths.ConfigurationFilePath);
        var pendingExists = Directory.Exists(_paths.PendingDirectoryPath);
        var logsExists = Directory.Exists(_paths.LogsDirectoryPath);
        var rootIsDirectory = rootExists;
        var configurationIsFile = configurationExists && !Directory.Exists(_paths.ConfigurationFilePath);
        var pendingIsDirectory = pendingExists && !File.Exists(_paths.PendingDirectoryPath);
        var logsIsDirectory = logsExists && !File.Exists(_paths.LogsDirectoryPath);
        var pathsTrusted = _paths.IsTrustedRootDirectory() &&
            _paths.IsTrustedPath(_paths.ConfigurationFilePath) &&
            _paths.IsTrustedPath(_paths.ConfigurationTemporaryFilePath) &&
            _paths.IsTrustedPath(_paths.PendingDirectoryPath) &&
            _paths.IsTrustedPath(_paths.LogsDirectoryPath);

        return new StartupInspection(
            rootExists,
            configurationExists,
            pendingExists,
            logsExists,
            rootIsDirectory,
            configurationIsFile,
            pendingIsDirectory,
            logsIsDirectory,
            pathsTrusted);
    }

    public async Task<StartupGateResult> InitializeAfterConfirmationAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var before = Inspect();
        if (before.IsComplete)
        {
            try
            {
                _ = await _profileStore
                    .LoadRequiredAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (InvalidDataException)
            {
                return new StartupGateResult(
                    StartupGateStatus.Failed,
                    before,
                    "Vela 配置无效，未继续启动。请修复配置后重试。");
            }

            return new StartupGateResult(
                StartupGateStatus.Ready,
                before,
                "Vela 数据目录已就绪。");
        }

        if (!_paths.IsTrustedRootDirectory() ||
            !_paths.IsTrustedPath(_paths.PendingDirectoryPath) ||
            !_paths.IsTrustedPath(_paths.LogsDirectoryPath) ||
            !_paths.IsTrustedPath(_paths.ConfigurationFilePath) ||
            !_paths.IsTrustedPath(_paths.ConfigurationTemporaryFilePath))
        {
            return new StartupGateResult(
                StartupGateStatus.Failed,
                before,
                "Vela 数据目录无法通过安全检查，未完成初始化。");
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(_paths.RootDirectory);

            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(_paths.PendingDirectoryPath);
            Directory.CreateDirectory(_paths.LogsDirectoryPath);

            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(_paths.ConfigurationFilePath))
            {
                await _profileStore
                    .SaveIfMissingAsync(
                        JsonProfileStore.CreateInitialState(),
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            var after = Inspect();
            if (!after.IsComplete)
            {
                return new StartupGateResult(
                    StartupGateStatus.Failed,
                    after,
                    "Vela 数据目录初始化未完成，未继续启动。");
            }

            try
            {
                _ = await _profileStore
                    .LoadRequiredAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (InvalidDataException)
            {
                return new StartupGateResult(
                    StartupGateStatus.Failed,
                    after,
                    "Vela 配置无效，未继续启动。请修复配置后重试。");
            }

            return new StartupGateResult(
                StartupGateStatus.Initialized,
                after,
                "Vela 数据目录初始化完成。");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return new StartupGateResult(
                StartupGateStatus.Failed,
                Inspect(),
                "Vela 数据目录初始化失败，未继续启动。");
        }
    }
}
