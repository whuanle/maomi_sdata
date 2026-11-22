using System.Text.Json;
using Microsoft.Extensions.Logging;
using SData.Infra.Exceptions;

namespace SData.Settings.Services;

/// <summary>
/// 串行保存运行参数：DuckDB 线程与并发先在运行时应用，再持久化；持久化失败则恢复原运行值.
/// </summary>
public sealed class RuntimeSettingsService : IDisposable
{
    private readonly ISystemSettingsProvider _settings;
    private readonly IDuckDbRuntimeSettings _duckDb;
    private readonly ILogger<RuntimeSettingsService> _logger;
    private readonly SemaphoreSlim _saveLock = new(1, 1);

    /// <summary>
    /// Initializes a new instance of the <see cref="RuntimeSettingsService"/> class.
    /// </summary>
    /// <param name="settings">系统设置.</param>
    /// <param name="duckDb">DuckDB 实例运行参数通道.</param>
    /// <param name="logger">日志.</param>
    public RuntimeSettingsService(ISystemSettingsProvider settings, IDuckDbRuntimeSettings duckDb, ILogger<RuntimeSettingsService> logger)
    {
        _settings = settings;
        _duckDb = duckDb;
        _logger = logger;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _saveLock.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// 保存运行参数（全量替换）.
    /// </summary>
    /// <param name="runtime">运行参数全量.</param>
    /// <param name="cancellationToken">取消.</param>
    /// <returns>保存后的运行参数.</returns>
    public async Task<RuntimeSettings> SaveAsync(RuntimeSettings runtime, CancellationToken cancellationToken)
    {
        await _saveLock.WaitAsync(cancellationToken);
        try
        {
            var previous = _settings.GetRuntime();
            try
            {
                await ApplyDuckDbAsync(runtime.DuckDb, cancellationToken);
                await _settings.SaveAsync(SettingKeys.Runtime, JsonSerializer.Serialize(runtime, SystemSettingsProvider.JsonOptions), cancellationToken);
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "保存运行参数失败，尝试恢复 DuckDB 线程与并发");
                try
                {
                    await ApplyDuckDbAsync(previous.DuckDb, CancellationToken.None);
                }
                catch (Exception rollbackException)
                {
                    _logger.LogCritical(rollbackException, "DuckDB 线程或并发恢复失败，当前参数与持久化设置可能不一致");
                    throw BusinessException.Throw(503, "运行参数保存失败且恢复失败，请检查服务日志与当前生效值");
                }

                if (exception is OperationCanceledException)
                {
                    throw;
                }

                throw BusinessException.Throw(503, "运行参数保存失败，已恢复原运行设置，请检查服务日志");
            }

            _logger.LogInformation("运行参数已保存（DuckDB threads={Threads}, max_queries={MaxQueries}）", runtime.DuckDb.Threads, runtime.DuckDb.MaxConcurrentQueries);
            return runtime;
        }
        finally
        {
            _saveLock.Release();
        }
    }

    private async Task ApplyDuckDbAsync(DuckDbTuningOptions duckDb, CancellationToken cancellationToken)
    {
        await _duckDb.ApplyThreadsAsync(duckDb.Threads, cancellationToken);
        _duckDb.ApplyMaxConcurrentQueries(duckDb.MaxConcurrentQueries);
    }
}
