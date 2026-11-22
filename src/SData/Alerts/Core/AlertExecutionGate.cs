using System.Diagnostics;
using SData.Infra.Exceptions;
using SData.Settings;
using SData.Store;
using SData.Store.Models;

namespace SData.Alerts;

/// <summary>预览和调度共用单槽及墙钟预算；执行完成前不释放槽（预算与超时取运行参数，保存即生效）.</summary>
public sealed class AlertExecutionGate(ISystemSettingsProvider settings, TimeProvider clock)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private long _minute = -1;
    private double _spent;

    public async Task<AlertCountResult> ExecuteAsync(IAnalyticsExecutor executor, AlertCountQuery query, CancellationToken token)
    {
        var alerts = settings.GetRuntime().Alerts;
        if (!alerts.Enabled || !await _gate.WaitAsync(0, token))
        {
            throw BusinessException.Throw(429, "告警执行器繁忙或已停用，请稍后重试");
        }

        var watch = Stopwatch.StartNew();
        try
        {
            var minute = clock.GetUtcNow().ToUnixTimeSeconds() / 60;
            if (_minute != minute)
            {
                _minute = minute;
                _spent = 0;
            }

            var remaining = Math.Clamp(alerts.BudgetSecondsPerMinute, 1, 30) - _spent;
            if (remaining <= 0)
            {
                throw BusinessException.Throw(429, "本分钟告警评估预算已用尽");
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Min(remaining, Math.Clamp(alerts.QueryTimeoutSeconds, 1, 10))));
            return await executor.ExecuteAlertCountAsync(query, timeout.Token);
        }
        finally
        {
            _spent += watch.Elapsed.TotalSeconds;
            _gate.Release();
        }
    }
}
