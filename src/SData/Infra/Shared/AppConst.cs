using System.Diagnostics;

namespace SData.Infra;

/// <summary>
/// 全局常量与路径.
/// </summary>
public static class AppConst
{
    /// <summary>
    /// ActivitySource 名称，供 OpenTelemetry 采集内部链路.
    /// </summary>
    public const string ActivitySourceName = "SData";

    /// <summary>
    /// 服务名（OTel resource).
    /// </summary>
    public const string ServiceName = "sdata";

    /// <summary>
    /// 全局 ActivitySource.
    /// </summary>
    public static readonly ActivitySource ActivitySource = new(ActivitySourceName);

    /// <summary>
    /// 默认数据目录（sqlite + duckdb 文件所在目录）.
    /// </summary>
    public const string DefaultDataDirectory = "data";

    /// <summary>
    /// 配置目录.
    /// </summary>
    public const string ConfigsDirectory = "configs";

    /// <summary>
    /// 站点内容根目录，宿主启动时赋值（configs、data、rsa key 均相对该目录）.
    /// </summary>
    public static string ContentRoot { get; set; } = AppContext.BaseDirectory;

    /// <summary>
    /// 获取站点内相对路径的绝对路径.
    /// </summary>
    /// <param name="segments">相对路径片段.</param>
    /// <returns>绝对路径.</returns>
    public static string GetPath(params string[] segments)
    {
        var path = ContentRoot;
        foreach (var segment in segments)
        {
            path = Path.Combine(path, segment);
        }

        return path;
    }
}
