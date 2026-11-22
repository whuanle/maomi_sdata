using System.Text.Json;
using Microsoft.Extensions.Logging;
using SData.Infra.Exceptions;

namespace SData.Settings.Services;

/// <summary>串行协调实例参数与 SQLite 覆盖；持久化失败恢复原运行参数.</summary>
public sealed class DuckDbSettingsService : IDisposable
{
    private readonly ISystemSettingsProvider _settings;
    private readonly IDuckDbRuntimeSettings _runtime;
    private readonly ILogger<DuckDbSettingsService> _logger;
    private readonly SemaphoreSlim _saveLock = new(1, 1);

    public DuckDbSettingsService(ISystemSettingsProvider settings, IDuckDbRuntimeSettings runtime, ILogger<DuckDbSettingsService> logger)
    {
        _settings = settings;
        _runtime = runtime;
        _logger = logger;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _saveLock.Dispose();
        GC.SuppressFinalize(this);
    }

    public async Task<DuckDbSettingsResponse> SaveAsync(string memoryLimit, CancellationToken cancellationToken)
    {
        if (!DuckDbMemoryLimit.TryNormalize(memoryLimit, out var normalized))
        {
            throw BusinessException.Throw(400, "内存限制必须是有效的正数容量，例如 512MB 或 2GB");
        }

        await _saveLock.WaitAsync(cancellationToken);
        try
        {
            var previous = _runtime.MemoryLimit;
            try
            {
                await _runtime.ApplyMemoryLimitAsync(normalized, cancellationToken);
                await _settings.SaveAsync(SettingKeys.DuckDb,
                    JsonSerializer.Serialize(new DuckDbSettings { MemoryLimit = normalized }, SystemSettingsProvider.JsonOptions), cancellationToken);
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "保存 DuckDB 内存限制 {MemoryLimit} 失败，尝试恢复 {Previous}", normalized, previous);
                try
                {
                    await _runtime.ApplyMemoryLimitAsync(previous, CancellationToken.None);
                }
                catch (Exception rollbackException)
                {
                    _logger.LogCritical(rollbackException, "DuckDB 内存限制恢复失败，当前参数与持久化设置可能不一致");
                    throw BusinessException.Throw(503, "内存限制保存失败且恢复失败，请检查服务日志与当前生效值");
                }

                if (exception is OperationCanceledException)
                {
                    throw;
                }

                throw BusinessException.Throw(503, "内存限制保存失败，已恢复原运行设置，请检查服务日志");
            }

            _logger.LogInformation("DuckDB 内存限制已保存为 {MemoryLimit}，实际生效 {EffectiveMemoryLimit}", normalized, _runtime.EffectiveMemoryLimit);
            return new DuckDbSettingsResponse { MemoryLimit = normalized, EffectiveMemoryLimit = _runtime.EffectiveMemoryLimit };
        }
        finally
        {
            _saveLock.Release();
        }
    }
}
