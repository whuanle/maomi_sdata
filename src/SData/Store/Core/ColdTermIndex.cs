using System.Text.RegularExpressions;
using DuckDB.NET.Data;
using SData.Infra;

namespace SData.Store.Core;

/// <summary>
/// 冷存档 day 级词项索引（token + 中文二元组存在性索引，T518）：
/// 归档时从天表 body 提取 distinct 词项写入 sidecar parquet，并在 data/cold-idx/{domain}/ 留本地镜像；
/// 查询带关键词且命中冷层时，先扫本地小索引求「词项全部存在的候选天」，把 S3 下载/扫描范围裁到候选天.
/// 索引是超集过滤（词项都在 ≠ 一定命中），最终精确匹配仍由对象内 LIKE 完成，检索语义不变.
/// 词项规则：ASCII/数字按非字母数字切分取 distinct 小写 token（长度≥2）；CJK 连续段取相邻二元组（覆盖任意中文子串）.
/// </summary>
public static class ColdTermIndex
{
    private static readonly Regex AsciiTokenRegex = new("[A-Za-z0-9]+", RegexOptions.Compiled);
    private static readonly Regex CjkRunRegex = new("[\u4e00-\u9fff]{2,}", RegexOptions.Compiled);

    /// <summary>
    /// 归档对象键 → 索引对象键（logs/20260101.parquet → logs/20260101.idx.parquet）.
    /// </summary>
    /// <param name="objectKey">归档对象键（s3:// 或本地相对路径）.</param>
    /// <returns>索引对象键.</returns>
    public static string ToIdxKey(string objectKey)
    {
        if (!objectKey.EndsWith(".parquet", StringComparison.Ordinal))
        {
            return objectKey + ".idx.parquet";
        }

        return objectKey[..^".parquet".Length] + ".idx.parquet";
    }

    /// <summary>
    /// 索引本地镜像路径（data/cold-idx/{domain}/{shard}.idx.parquet，归档时直接落本地，查询零网络；
    /// shardName 为分片名：day 粒度 = yyyyMMdd，hour 粒度 = yyyyMMddHH）.
    /// </summary>
    /// <param name="domainFolder">域文件夹（logs/traces/metrics/iot）.</param>
    /// <param name="shardName">分片名.</param>
    /// <returns>本地镜像绝对路径.</returns>
    public static string LocalMirrorPath(string domainFolder, string shardName)
    {
        return Path.Combine(AppConst.GetPath(), "data", "cold-idx", domainFolder, $"{shardName}.idx.parquet");
    }

    /// <summary>
    /// 从索引文件路径提取分片名（{shard}.idx.parquet → shard；非索引文件返回 null）.
    /// </summary>
    /// <param name="filePath">文件路径或文件名.</param>
    /// <returns>分片名.</returns>
    public static string? ShardNameFromIdxFile(string filePath)
    {
        var name = Path.GetFileName(filePath);
        return name.EndsWith(".idx.parquet", StringComparison.Ordinal)
            ? name[..^".idx.parquet".Length]
            : null;
    }

    /// <summary>
    /// 从用户关键词提取可索引词项（ASCII token + CJK 二元组；与 BuildIndexSql 的提取规则同源）.
    /// </summary>
    /// <param name="keyword">用户关键词（原文子串语义）.</param>
    /// <returns>ASCII token 列表与 CJK 二元组列表.</returns>
    public static (List<string> AsciiTokens, List<string> CjkBigrams) ExtractTerms(string keyword)
    {
        var tokens = new List<string>();
        var bigrams = new List<string>();
        if (string.IsNullOrWhiteSpace(keyword))
        {
            return (tokens, bigrams);
        }

        foreach (var match in AsciiTokenRegex.Matches(keyword.ToLowerInvariant()))
        {
            var token = match.ToString();
            if (token.Length >= 2)
            {
                tokens.Add(token);
            }
        }

        foreach (var match in CjkRunRegex.Matches(keyword))
        {
            var run = match.ToString();
            for (var i = 0; i + 2 <= run.Length; i++)
            {
                bigrams.Add(run.Substring(i, 2));
            }
        }

        return (tokens, bigrams);
    }

    /// <summary>
    /// 归档时构建索引 sidecar 的 COPY 语句（全部内部标识符，无用户输入；路径单引号转义）.
    /// </summary>
    /// <param name="table">天表名.</param>
    /// <param name="destination">索引文件目标路径.</param>
    /// <param name="codec">压缩编码.</param>
    /// <returns>COPY SQL.</returns>
    public static string BuildIndexSql(string table, string destination, string codec)
    {
        var escaped = destination.Replace("'", "''");
        return $"""
COPY (
  WITH ascii AS (
    SELECT DISTINCT lower(t.token) AS term
    FROM (SELECT unnest(regexp_split_to_array(body, '[^a-zA-Z0-9]+')) AS token FROM {table} WHERE body IS NOT NULL) t
    WHERE length(t.token) >= 2
  ), runs AS (
    SELECT unnest(regexp_extract_all(body, '[一-鿿]+')) AS run FROM {table} WHERE body IS NOT NULL
  ), cjk AS (
    SELECT DISTINCT substr(run, p.i, 2) AS term FROM runs, range(1, length(run)) p(i) WHERE length(run) >= 2
  )
  SELECT term FROM ascii UNION SELECT term FROM cjk
) TO '{escaped}' (FORMAT PARQUET, COMPRESSION {codec})
""";
    }

    /// <summary>
    /// 候选分片查询：扫本地索引镜像（filename=true 带出文件名），HAVING 逐词项存在性 AND 交集.
    /// idxFiles 为显式文件列表（仅查询窗内有镜像的分片，不扫全域通配）；
    /// 外部值全部参数绑定；索引词项本身来自归档数据（非用户输入），LIKE 通配仍做转义.
    /// </summary>
    /// <param name="idxFiles">本地索引镜像文件路径列表.</param>
    /// <param name="asciiTokens">ASCII token 列表（子串包含语义）.</param>
    /// <param name="cjkBigrams">CJK 二元组列表（精确相等语义）.</param>
    /// <returns>SQL 与绑定参数.</returns>
    public static (string Sql, List<DuckDBParameter> Parameters) CandidateShardsSql(IReadOnlyList<string> idxFiles, List<string> asciiTokens, List<string> cjkBigrams)
    {
        var parameters = new List<DuckDBParameter>();
        var having = new List<string>();
        foreach (var token in asciiTokens)
        {
            var name = $"t{parameters.Count}";
            parameters.Add(new DuckDBParameter(name, $"%{EscapeLike(token)}%"));
            having.Add($"sum(CASE WHEN term LIKE ${name} THEN 1 ELSE 0 END) > 0");
        }

        foreach (var bigram in cjkBigrams)
        {
            var name = $"b{parameters.Count}";
            parameters.Add(new DuckDBParameter(name, bigram));
            having.Add($"sum(CASE WHEN term = ${name} THEN 1 ELSE 0 END) > 0");
        }

        var files = string.Join(", ", idxFiles.Select(file => $"'{file.Replace("'", "''")}'"));
        var sql = $"SELECT filename FROM read_parquet([{files}], filename=true) GROUP BY filename HAVING {string.Join(" AND ", having)}";
        return (sql, parameters);
    }

    private static string EscapeLike(string value)
    {
        return value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
    }
}
