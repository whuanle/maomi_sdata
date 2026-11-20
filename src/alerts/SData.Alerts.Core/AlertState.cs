using SData.Database.Entities;

namespace SData.Alerts;

/// <summary>对齐窗口与状态转换；错误和缺口不能触发恢复.</summary>
public static class AlertState
{
    public static long WindowEnd(DateTimeOffset now, int minutes, int delay)
    {
        var step = minutes * 60L;
        return (now.ToUnixTimeSeconds() - delay * 60L) / step * step;
    }

    public static string? Apply(AlertRuleEntity rule, long window, long count)
    {
        var hit = rule.Operator switch
        {
            "gte" => count >= rule.Threshold,
            "gt" => count > rule.Threshold,
            "lte" => count <= rule.Threshold,
            "lt" => count < rule.Threshold,
            _ => false,
        };
        var contiguous = rule.LastWindow + rule.WindowMinutes * 60L == window && rule.Failures == 0;
        rule.Hits = hit ? (contiguous ? rule.Hits + 1 : 1) : 0;
        rule.LastWindow = window;
        rule.Failures = 0;
        rule.Error = string.Empty;
        rule.NextAttempt = 0;
        if (hit)
        {
            if (rule.Hits >= rule.ConsecutiveWindows)
            {
                var changed = !rule.Firing;
                rule.Firing = true;
                rule.Status = "firing";
                return changed ? "firing" : null;
            }

            rule.Status = rule.Firing ? "firing" : "pending";
            return null;
        }

        var recovered = rule.Firing;
        rule.Firing = false;
        rule.Status = "normal";
        return recovered ? "resolved" : null;
    }
}
