using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace SData.Settings.Services;

/// <summary>
/// 串行保存运行参数（全量替换持久化到 system:runtime；性能参数不在此列，由系统自动调控）.
/// </summary>
public sealed class RuntimeSettingsService : IDisposable
{
    private readonly ISystemSettingsProvider _settings;
    private readonly ILogger<RuntimeSettingsService> _logger;
    private readonly SemaphoreSlim _saveLock = new(1, 1);

    /// <summary>
    /// Initializes a new instance of the <see cref="RuntimeSettingsService"/> class.
    /// </summary>
    /// <param name="settings">系统设置.</param>
    /// <param name="logger">日志.</param>
    public RuntimeSettingsService(ISystemSettingsProvider settings, ILogger<RuntimeSettingsService> logger)
    {
        _settings = settings;
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
            await _settings.SaveAsync(SettingKeys.Runtime, JsonSerializer.Serialize(runtime, SystemSettingsProvider.JsonOptions), cancellationToken);
            _logger.LogInformation("运行参数已保存");
            return runtime;
        }
        finally
        {
            _saveLock.Release();
        }
    }
}
