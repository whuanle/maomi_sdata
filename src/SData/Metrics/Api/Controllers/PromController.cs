using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SData.Metrics;

namespace SData.Metrics.Api.Controllers;

/// <summary>
/// Prometheus 兼容查询 API（T711）：GET /api/v1/query、GET /api/v1/query_range，Prometheus JSON 形状.
/// Grafana Prometheus 数据源将 server URL 指向本服务根即可对接.
/// </summary>
[ApiController]
[Route("v1")]
[Authorize]
public class PromController : ControllerBase
{
    private readonly IPromqlExecutor _executor;

    /// <summary>
    /// Initializes a new instance of the <see cref="PromController"/> class.
    /// </summary>
    /// <param name="executor">PromQL 执行器.</param>
    public PromController(IPromqlExecutor executor)
    {
        _executor = executor;
    }

    /// <summary>
    /// 即时查询（Prometheus /api/v1/query 兼容）.
    /// </summary>
    /// <param name="query">PromQL 表达式.</param>
    /// <param name="time">求值时间（RFC3339/Unix 秒；缺省当前时间；向前回看 5 分钟窗口）.</param>
    /// <param name="cancellationToken">取消.</param>
    /// <returns>Prometheus JSON 形状.</returns>
    [HttpGet("query")]
    public async Task<IActionResult> Query([FromQuery(Name = "query")] string? query, [FromQuery] string? time, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return PromError("bad_data", "query 参数不能为空");
        }

        var to = ParseTime(time) ?? DateTimeOffset.UtcNow;
        var from = to.AddMinutes(-5);
        try
        {
            var result = await _executor.ExecuteAsync(query, from, to, null, cancellationToken);
            return Ok(new
            {
                status = "success",
                data = new { resultType = result.ResultType, result = Shape(result) },
            });
        }
        catch (PromqlParseException ex)
        {
            return PromError("bad_data", ex.Message);
        }
    }

    /// <summary>
    /// 区间查询（Prometheus /api/v1/query_range 兼容）.
    /// </summary>
    /// <param name="query">PromQL 表达式.</param>
    /// <param name="start">开始时间.</param>
    /// <param name="end">结束时间.</param>
    /// <param name="step">步长（如 15s/1m/1h 或秒数）.</param>
    /// <param name="cancellationToken">取消.</param>
    /// <returns>Prometheus JSON 形状.</returns>
    [HttpGet("query_range")]
    public async Task<IActionResult> QueryRange([FromQuery(Name = "query")] string? query, [FromQuery] string? start, [FromQuery] string? end, [FromQuery] string? step, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return PromError("bad_data", "query 参数不能为空");
        }

        if (!TryParseStep(step, out var stepSpan) || stepSpan <= TimeSpan.Zero)
        {
            return PromError("bad_data", "step 参数无效（示例 15s/1m/1h 或秒数）");
        }

        var from = ParseTime(start);
        var to = ParseTime(end);
        if (from == null || to == null || to <= from)
        {
            return PromError("bad_data", "start/end 参数无效（RFC3339 或 Unix 秒，且 end > start）");
        }

        try
        {
            var result = await _executor.ExecuteAsync(query, from.Value, to.Value, stepSpan, cancellationToken);
            return Ok(new
            {
                status = "success",
                data = new { resultType = result.ResultType, result = Shape(result) },
            });
        }
        catch (PromqlParseException ex)
        {
            return PromError("bad_data", ex.Message);
        }
    }

    private static object[] Shape(PromqlResult result)
    {
        return result.ResultType == "matrix"
            ? [.. result.Result.Select(x => (object)new
            {
                metric = x.Metric,
                values = x.Values.Select(v => new object[] { v.Time.ToUnixTimeSeconds(), v.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) }).ToArray(),
            })]
            : [.. result.Result.Select(x => (object)new
            {
                metric = x.Metric,
                value = x.Value == null ? null : new object[] { x.Value.Value.Time.ToUnixTimeSeconds(), x.Value.Value.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) },
            })];
    }

    private IActionResult PromError(string errorType, string message)
    {
        return Ok(new { status = "error", errorType, error = message });
    }

    private static DateTimeOffset? ParseTime(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        if (double.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out var unixSeconds))
        {
            return DateTimeOffset.FromUnixTimeMilliseconds((long)(unixSeconds * 1000));
        }

        return DateTimeOffset.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed) ? parsed : null;
    }

    private static bool TryParseStep(string? step, out TimeSpan span)
    {
        span = TimeSpan.Zero;
        if (string.IsNullOrEmpty(step))
        {
            return false;
        }

        // 纯数字按秒
        if (double.TryParse(step, System.Globalization.CultureInfo.InvariantCulture, out var seconds))
        {
            span = TimeSpan.FromSeconds(seconds);
            return true;
        }

        // 数字+单位（ms/s/m/h/d/w）
        var match = System.Text.RegularExpressions.Regex.Match(step, """^(\d+(?:\.\d+)?)(ms|s|m|h|d|w)$""");
        if (!match.Success)
        {
            return false;
        }

        var value = double.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        span = match.Groups[2].Value switch
        {
            "ms" => TimeSpan.FromMilliseconds(value),
            "s" => TimeSpan.FromSeconds(value),
            "m" => TimeSpan.FromMinutes(value),
            "h" => TimeSpan.FromHours(value),
            "d" => TimeSpan.FromDays(value),
            "w" => TimeSpan.FromDays(value * 7),
            _ => TimeSpan.Zero,
        };
        return span > TimeSpan.Zero;
    }
}
