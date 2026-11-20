namespace SData.Store;

/// <summary>分析工作台的 DuckDB 表目录.</summary>
public interface IAnalyticsCatalog
{
    /// <summary>读取当前物理表及实际字段元数据，不扫描业务数据.</summary>
    Task<IReadOnlyList<AnalyticsTableItem>> GetTablesAsync(CancellationToken cancellationToken = default);
}

/// <summary>物理表元数据.</summary>
public class AnalyticsTableItem
{
    /// <summary>Schema.</summary>
    public string Schema { get; set; } = string.Empty;

    /// <summary>表名.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>数据域；其它表为空.</summary>
    public string? Domain { get; set; }

    /// <summary>UTC 分表日期.</summary>
    public DateTimeOffset? Day { get; set; }

    /// <summary>实际字段.</summary>
    public List<AnalyticsColumnItem> Columns { get; set; } = new();
}

/// <summary>实际字段元数据.</summary>
public class AnalyticsColumnItem
{
    /// <summary>字段名.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>DuckDB 类型.</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>允许空值.</summary>
    public bool Nullable { get; set; }
}
