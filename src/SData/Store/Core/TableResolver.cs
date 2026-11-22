using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SData.Infra;
using SData.Infra.Configuration;
using SData.Settings;
using SData.Store.QueryDsl;

namespace SData.Store.Core;

/// <summary>
/// 默认表解析器（契约 C2，统一查询管线的唯一表解析真相源）：
/// 本地 = GetDayTables 现存天表；冷层 = 台账内存镜像（ColdLedgerCache，零 SQLite 往返）窗内分片 →
/// 缓存命中返回本地缓存路径，未命中 S3 直读（并 fire-and-forget 后台预热）/ 本地冷存绝对路径直读；
/// 台账为空时兜底：S3 配置存在先经 glob() 验存、命中才输出域级通配（手工放置对象场景；
/// 零命中/验存失败 fail-open 跳过冷层——read_parquet 对零命中通配在 prepare 阶段抛 IO Error，
/// 会把本地天表可查的搜索整体打成 500；探测按配置指纹+域缓存并熔断，S3 故障不逐查询拖慢搜索），
/// 未配 S3 输出本地冷存目录通配（data/cold 存在 parquet 时）.
/// ColdFiles 与 ColdParquetGlobs 互斥（防同一对象重复扫描出重复行）.
/// 带关键词时经词项索引本地镜像做候选分片裁剪：只扫查询窗内有镜像分片的 idx 文件（显式列表，不全域 glob），
/// 无镜像的分片保守保留（保证超集正确性）；索引是超集过滤，对象内 LIKE 精确匹配，检索语义不变.
/// </summary>
public sealed class TableResolver : ITableResolver
{
    /// <summary>验存命中（有对象）结果缓存窗口.</summary>
    internal static readonly TimeSpan ProbeHitTtl = TimeSpan.FromSeconds(60);

    /// <summary>验存零命中（桶内无对象）缓存窗口.</summary>
    internal static readonly TimeSpan ProbeEmptyTtl = TimeSpan.FromSeconds(30);

    /// <summary>验存异常（端点不可达/鉴权失败/桶不存在）熔断窗口：期内不再触碰 S3，保证 S3 故障不拖慢本地查询.</summary>
    internal static readonly TimeSpan ProbeBrokenTtl = TimeSpan.FromMinutes(5);

    private readonly DuckDbConnectionManager _connectionManager;
    private readonly SystemOptions _systemOptions;
    private readonly ISystemSettingsProvider _settingsProvider;
    private readonly ColdLedgerCache _ledgerCache;
    private readonly ColdCacheService _coldCache;
    private readonly ILogger<TableResolver> _logger;

    /// <summary>
    /// 兜底通配验存缓存（键 = S3 配置指纹 + 域文件夹，配置变更自动换键）：
    /// HasFiles true=有对象 / false=零命中 / null=验存异常（熔断）。
    /// 探测发生在查询请求路径上，S3 故障时一次探测可能拖住整次查询——
    /// 按窗口缓存探测结果，每个窗口每域最多触碰一次 S3.
    /// </summary>
    private readonly ConcurrentDictionary<string, (bool? HasFiles, DateTimeOffset ExpiresAt)> _globProbes = new(StringComparer.Ordinal);

    /// <summary>
    /// Initializes a new instance of the <see cref="TableResolver"/> class.
    /// </summary>
    /// <param name="connectionManager">连接管理器.</param>
    /// <param name="systemOptions">系统配置（S3 段）.</param>
    /// <param name="settingsProvider">运行时系统设置（S3 热生效）.</param>
    /// <param name="ledgerCache">台账内存镜像.</param>
    /// <param name="coldCache">冷数据本地缓存.</param>
    /// <param name="logger">日志.</param>
    public TableResolver(DuckDbConnectionManager connectionManager, SystemOptions systemOptions, ISystemSettingsProvider settingsProvider, ColdLedgerCache ledgerCache, ColdCacheService coldCache, ILogger<TableResolver> logger)
    {
        _connectionManager = connectionManager;
        _systemOptions = systemOptions;
        _settingsProvider = settingsProvider;
        _ledgerCache = ledgerCache;
        _coldCache = coldCache;
        _logger = logger;
    }

    /// <summary>
    /// 解析查询区间涉及的数据源（统一查询管线唯一入口；异步全链路，无 sync-over-async）.
    /// </summary>
    /// <param name="domain">数据域.</param>
    /// <param name="from">起始（含）.</param>
    /// <param name="to">结束（不含）.</param>
    /// <param name="searchKeyword">全文检索关键词（可选，词项索引候选分片裁剪）.</param>
    /// <param name="cancellationToken">取消.</param>
    /// <returns>本地表集合与冷层路径（逐分片精确路径优先，通配兜底）.</returns>
    public async Task<ResolvedTables> ResolveAsync(DataDomain domain, DateTimeOffset from, DateTimeOffset to, string? searchKeyword = null, CancellationToken cancellationToken = default)
    {
        var prefix = domain switch
        {
            DataDomain.Logs => TimeTables.LogsPrefix,
            DataDomain.Traces => TimeTables.TracesPrefix,
            DataDomain.Metrics => TimeTables.MetricsPrefix,
            DataDomain.Iot => TimeTables.IotPrefix,
            DataDomain.RumSession => TimeTables.RumSessionPrefix,
            DataDomain.RumEvent => TimeTables.RumEventPrefix,
            DataDomain.RumReplay => TimeTables.RumReplayPrefix,
            _ => throw new ArgumentOutOfRangeException(nameof(domain)),
        };

        var ledger = await _ledgerCache.GetAsync(ArchiveService.DomainName(domain), cancellationToken);
        foreach (var table in _connectionManager.GetManagedTables(prefix).Where(TimeTables.IsArchive))
        {
            if (ledger.Any(entry => entry.ObjectKey.Replace("\\", "/").Contains("/" + TimeTables.ArchiveId(table) + "/", StringComparison.Ordinal)))
            {
                _connectionManager.HidePublishedSnapshot(table);
            }
        }

        var local = _connectionManager.GetDayTables(prefix, from, to);
        var coldFiles = await ResolveColdFilesAsync(domain, from, to, searchKeyword, cancellationToken);
        var hasLedger = await _ledgerCache.HasEntriesAsync(ArchiveService.DomainName(domain), cancellationToken);
        var coldGlobs = !hasLedger && coldFiles.Count == 0 ? await ResolveFallbackGlobsAsync(domain, cancellationToken) : new List<string>();
        return new ResolvedTables(local, coldGlobs) { ColdFiles = coldFiles };
    }

    /// <summary>
    /// 台账镜像驱动的窗内逐分片冷层路径（天表同口径：day &gt;= from.Date 且 day &lt; to.Date）.
    /// </summary>
    private async Task<List<string>> ResolveColdFilesAsync(DataDomain domain, DateTimeOffset from, DateTimeOffset to, string? searchKeyword, CancellationToken cancellationToken)
    {
        var entries = new List<(string ShardName, string Path)>();
        foreach (var entry in await _ledgerCache.GetAsync(ArchiveService.DomainName(domain), cancellationToken))
        {
            if (entry.RowCount <= 0 || entry.Day.AddDays(1) <= from || entry.Day >= to)
            {
                continue;
            }

            string path;
            if (!entry.ObjectKey.StartsWith("s3://", StringComparison.Ordinal))
            {
                // 本地冷存对象：直读绝对路径，不进缓存（本地磁盘已是最终介质）
                path = Path.Combine(AppConst.GetPath(), entry.ObjectKey);
            }
            else
            {
                var cached = _coldCache.TryGetLocalPath(entry.ObjectKey);
                if (cached != null)
                {
                    path = cached;
                }
                else
                {
                    // S3 直读 + fire-and-forget 后台预热（本轮查询直读，下轮命中本地）
                    path = entry.ObjectKey;
                    _coldCache.Warm(entry.ObjectKey);
                }
            }

            entries.Add((entry.ShardName, path));
        }

        if (entries.Count == 0 || string.IsNullOrWhiteSpace(searchKeyword))
        {
            return entries.Select(entry => entry.Path).ToList();
        }

        var pruned = await PruneByTermIndexAsync(domain, entries, searchKeyword, cancellationToken);
        return pruned.Select(entry => entry.Path).ToList();
    }

    /// <summary>
    /// 词项索引候选分片裁剪：只保留「词项全部存在」或有索引镜像的分片（无镜像的分片保守保留，防漏）；
    /// idx 文件用显式列表（仅查询窗内有镜像的分片），不扫全域通配.
    /// </summary>
    private async Task<List<(string ShardName, string Path)>> PruneByTermIndexAsync(DataDomain domain, List<(string ShardName, string Path)> entries, string keyword, CancellationToken cancellationToken)
    {
        var (asciiTokens, cjkBigrams) = ColdTermIndex.ExtractTerms(keyword);
        if (asciiTokens.Count == 0 && cjkBigrams.Count == 0)
        {
            return entries;
        }

        var folder = ArchiveService.DomainName(domain);
        var indexed = entries
            .Where(entry => File.Exists(ColdTermIndex.LocalMirrorPath(folder, entry.ShardName)))
            .ToList();
        if (indexed.Count == 0)
        {
            return entries;
        }

        var idxFiles = indexed
            .Select(entry => ColdTermIndex.LocalMirrorPath(folder, entry.ShardName).Replace('\\', '/'))
            .ToList();
        var (sql, parameters) = ColdTermIndex.CandidateShardsSql(idxFiles, asciiTokens, cjkBigrams);
        var candidates = await _connectionManager.QueryAsync(sql, parameters, async reader =>
        {
            var shards = new HashSet<string>(StringComparer.Ordinal);
            while (await reader.ReadAsync(cancellationToken))
            {
                var shard = ColdTermIndex.ShardNameFromIdxFile(reader.GetString(0));
                if (shard != null)
                {
                    shards.Add(shard);
                }
            }

            return shards;
        }, cancellationToken);

        var indexedShards = indexed.Select(entry => entry.ShardName).ToHashSet(StringComparer.Ordinal);
        return entries
            .Where(entry => !indexedShards.Contains(entry.ShardName) || candidates.Contains(entry.ShardName))
            .ToList();
    }

    /// <summary>
    /// 台账为空时的兜底通配（S3 域级通配 / 本地冷存目录通配）；
    /// S3 通配输出前先经 glob() 验存（零命中/验存失败一律跳过冷层分支，保证本地查询可用），
    /// 探测结果按窗口缓存（命中 60s / 零命中 30s / 异常熔断 5min），窗口内不触碰 S3.
    /// </summary>
    private async Task<List<string>> ResolveFallbackGlobsAsync(DataDomain domain, CancellationToken cancellationToken)
    {
        var globs = new List<string>();
        var s3 = _settingsProvider.GetS3();
        if (!string.IsNullOrWhiteSpace(s3.Endpoint) && !string.IsNullOrWhiteSpace(s3.Bucket))
        {
            var prefixPath = string.IsNullOrWhiteSpace(s3.Prefix) ? string.Empty : s3.Prefix.TrimEnd('/') + "/";
            var glob = $"s3://{s3.Bucket}/{prefixPath}{DomainFolder(domain)}/*[0-9].parquet";
            var fingerprint = $"{s3.Endpoint}|{s3.Bucket}|{s3.Prefix}|{DomainFolder(domain)}";
            var now = DateTimeOffset.UtcNow;
            if (_globProbes.TryGetValue(fingerprint, out var probe) && probe.ExpiresAt > now)
            {
                // 缓存窗口内不再触碰 S3：命中=带通配；零命中/熔断=直接跳过冷层（S3 故障每次窗口最多探测一次，不逐查询付延迟）
                if (probe.HasFiles == true)
                {
                    globs.Add(glob);
                }

                return globs;
            }

            var hasFiles = await ProbeGlobAsync(glob, cancellationToken);
            if (hasFiles == true)
            {
                _globProbes[fingerprint] = (true, now + ProbeHitTtl);
                globs.Add(glob);
            }
            else if (hasFiles == false)
            {
                _globProbes[fingerprint] = (false, now + ProbeEmptyTtl);
            }
            else
            {
                _globProbes[fingerprint] = (null, now + ProbeBrokenTtl);
            }

            return globs;
        }

        // 未配置 S3（含本地冷存部署）：本地冷存目录存在归档对象时输出目录通配（T512 本地归档可查）
        var coldDir = Path.Combine(AppConst.GetPath(), "data", "cold", DomainFolder(domain));
        if (Directory.Exists(coldDir) && Directory.EnumerateFiles(coldDir, "*.parquet").Any(file => !file.EndsWith(".idx.parquet", StringComparison.Ordinal)))
        {
            globs.Add(Path.Combine(coldDir, "*[0-9].parquet").Replace('\\', '/'));
        }

        return globs;
    }

    /// <summary>
    /// S3 通配验存：glob() 至少命中 1 个对象返回 true，零命中返回 false，验存异常返回 null（熔断态）.
    /// glob 与 read_parquet 同引擎同 SECRET，验存结果与后续查询视角一致；零命中记 Debug；
    /// 端点不可达/鉴权错误/桶不存在等验存异常记 Warning（fail-open）——S3 配置问题在归档写入路径仍显性失败，
    /// 不应连累本地天表查询。通配值来自 S3 配置（管理员配置，非终端用户输入），单引号转义防 SQL 结构破坏
    /// （与冷层路径拼接同一惯例，见表占位符展开处）.
    /// </summary>
    private async Task<bool?> ProbeGlobAsync(string glob, CancellationToken cancellationToken)
    {
        try
        {
            var count = await _connectionManager.QueryAsync(
                $"SELECT count(*) FROM glob('{glob.Replace("'", "''")}')",
                null,
                async reader =>
                {
                    await reader.ReadAsync(cancellationToken);
                    return reader.GetInt64(0);
                },
                cancellationToken);
            if (count > 0)
            {
                return true;
            }

            _logger.LogDebug("S3 冷层兜底通配零命中，跳过冷层分支（桶内尚无该域对象）：{Glob}", glob);
            return false;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "S3 冷层兜底通配验存失败（端点不可达、鉴权错误或存储桶不存在？），跳过冷层分支以保证本地查询可用：{Glob}", glob);
            return null;
        }
    }

    private static string DomainFolder(DataDomain domain)
    {
        return domain switch
        {
            DataDomain.Logs => "logs",
            DataDomain.Traces => "traces",
            DataDomain.Metrics => "metrics",
            DataDomain.Iot => "iot",
            DataDomain.RumSession => "rum_session",
            DataDomain.RumEvent => "rum_event",
            DataDomain.RumReplay => "rum_replay",
            _ => throw new ArgumentOutOfRangeException(nameof(domain)),
        };
    }
}
