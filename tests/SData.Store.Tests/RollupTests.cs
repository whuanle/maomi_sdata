using global::Xunit;
using Microsoft.Extensions.Logging.Abstractions;
using SData.Infra;
using SData.Infra.Configuration;
using SData.Store;
using SData.Store.Core;
using SData.Store.Models;

namespace SData.Store.Tests;

/// <summary>
/// 指标 rollup 测试（T312，契约 C6）：DDL 幂等、窗口回填幂等、长区间分派与边界.
/// </summary>
public class RollupTests
{
    /// <summary>rollup 两表 DDL 幂等：bootstrapper 重复执行不报错且表存在.</summary>
    [Fact]
    public void Ddl_Idempotent()
    {
        using var harness = new RollupHarness();
        // ApplyAsync 在 ctor 已执行一次；再执行一次验证幂等（表不存在时 Count 会抛错）
        harness.Bootstrapper.ApplyAsync().GetAwaiter().GetResult();

        Assert.True(harness.Manager.CountAsync(TimeTables.Rollup5mTable).GetAwaiter().GetResult() >= 0);
        Assert.True(harness.Manager.CountAsync(TimeTables.Rollup1hTable).GetAwaiter().GetResult() >= 0);
    }

    /// <summary>窗口回填幂等：两轮回填同窗口，行数与聚合值稳定.</summary>
    [Fact]
    public async Task Backfill_Idempotent_Window()
    {
        using var harness = new RollupHarness();
        var t = RollupHarness.ClosedBucketTime();
        var batch = new List<IngestItem>
        {
            new(IngestKind.Metric, new MetricPointRecord { Name = "cpu", Timestamp = t, Value = 4, LabelsJson = """{"host":"n1"}""" }),
            new(IngestKind.Metric, new MetricPointRecord { Name = "cpu", Timestamp = t.AddSeconds(90), Value = 6, LabelsJson = """{"host":"n1"}""" }),
        };
        await harness.Writer.FlushAsync(batch, harness.Token);

        await harness.Service.BackfillAsync(harness.Token);
        var (rows1, sum1, count1) = await harness.Read1hAsync("cpu");
        await harness.Service.BackfillAsync(harness.Token);
        var (rows2, sum2, count2) = await harness.Read1hAsync("cpu");

        Assert.Equal(1, rows1);
        Assert.Equal(rows1, rows2);
        Assert.Equal(10.0, sum1, 6);
        Assert.Equal(sum1, sum2);
        Assert.Equal(2L, count1);
        Assert.Equal(count1, count2);
    }

    /// <summary>5m 表同样回填且粒度更细.</summary>
    [Fact]
    public async Task Backfill_5mTable_Filled()
    {
        using var harness = new RollupHarness();
        var t = RollupHarness.Closed5mBucketTime();
        var batch = new List<IngestItem>
        {
            new(IngestKind.Metric, new MetricPointRecord { Name = "mem", Timestamp = t, Value = 1 }),
            new(IngestKind.Metric, new MetricPointRecord { Name = "mem", Timestamp = t.AddMinutes(3), Value = 3 }),
        };
        await harness.Writer.FlushAsync(batch, harness.Token);

        await harness.Service.BackfillAsync(harness.Token);
        var count = await harness.Manager.QueryAsync(
            $"SELECT count(*) FROM {TimeTables.Rollup5mTable}",
            null,
            async reader =>
            {
                await reader.ReadAsync(harness.Token);
                return reader.GetInt64(0);
            }, harness.Token);
        Assert.True(count >= 1, "5m rollup 表应至少有 1 行");
    }

    /// <summary>跨度 > 24h 且 avg：走 rollup（单桶点，值 = sum/count）.</summary>
    [Fact]
    public async Task QueryRange_Beyond24h_Avg_DispatchesRollup()
    {
        using var harness = new RollupHarness();
        var t = RollupHarness.ClosedBucketTime();
        var batch = new List<IngestItem>
        {
            new(IngestKind.Metric, new MetricPointRecord { Name = "cpu", Timestamp = t, Value = 4 }),
            new(IngestKind.Metric, new MetricPointRecord { Name = "cpu", Timestamp = t.AddSeconds(30), Value = 6 }),
        };
        await harness.Writer.FlushAsync(batch, harness.Token);
        await harness.Service.BackfillAsync(harness.Token);

        var response = await harness.MetricStore.QueryRangeAsync(new MetricSeriesQuery
        {
            Name = "cpu",
            Start = t.AddHours(-26),
            End = t.AddHours(1),
            Aggregation = MetricAggregation.Avg,
            StepSeconds = 60,
        }, harness.Token);

        var series = Assert.Single(response.SeriesList);
        var point = Assert.Single(series.Points);
        Assert.Equal(5.0, point.Value!.Value, 6);
        // rollup 路径步长下限 1h：桶时间戳为 1h 对齐
        Assert.Equal(0, point.TimestampMs % 3_600_000);
    }

    /// <summary>跨度 > 24h 但 p95：回退原始表（多点原始值）.</summary>
    [Fact]
    public async Task QueryRange_Beyond24h_P95_FallsBackRaw()
    {
        using var harness = new RollupHarness();
        var t = RollupHarness.ClosedBucketTime();
        var batch = new List<IngestItem>
        {
            new(IngestKind.Metric, new MetricPointRecord { Name = "cpu", Timestamp = t, Value = 4 }),
            new(IngestKind.Metric, new MetricPointRecord { Name = "cpu", Timestamp = t.AddSeconds(90), Value = 6 }),
        };
        await harness.Writer.FlushAsync(batch, harness.Token);
        await harness.Service.BackfillAsync(harness.Token);

        var response = await harness.MetricStore.QueryRangeAsync(new MetricSeriesQuery
        {
            Name = "cpu",
            Start = t.AddHours(-26),
            End = t.AddHours(1),
            Aggregation = MetricAggregation.P95,
            StepSeconds = 60,
        }, harness.Token);

        // 原始表路径按 60s 步长分桶 → 两个不同分钟的独立点
        var series = Assert.Single(response.SeriesList);
        Assert.Equal(2, series.Points.Count);
    }

    /// <summary>跨度恰好 24h：不派发 rollup（原始表多点）.</summary>
    [Fact]
    public async Task QueryRange_Exactly24h_UsesRaw()
    {
        using var harness = new RollupHarness();
        var t = RollupHarness.ClosedBucketTime();
        var first = t.AddMinutes(-30);
        var batch = new List<IngestItem>
        {
            new(IngestKind.Metric, new MetricPointRecord { Name = "cpu", Timestamp = first, Value = 4 }),
            new(IngestKind.Metric, new MetricPointRecord { Name = "cpu", Timestamp = first.AddSeconds(90), Value = 6 }),
        };
        await harness.Writer.FlushAsync(batch, harness.Token);
        await harness.Service.BackfillAsync(harness.Token);

        // end = t：跨度恰好 24h（不满足 > 24h），且 first 两点均落在 [start, end) 内
        var response = await harness.MetricStore.QueryRangeAsync(new MetricSeriesQuery
        {
            Name = "cpu",
            Start = t.AddHours(-24),
            End = t,
            Aggregation = MetricAggregation.Avg,
            StepSeconds = 60,
        }, harness.Token);

        var series = Assert.Single(response.SeriesList);
        Assert.Equal(2, series.Points.Count);
    }

    /// <summary>rollup 测试夹具：临时库 + 写入器 + RollupService（BackfillAsync 内部方法直调）.</summary>
    private sealed class RollupHarness : IDisposable
    {
        /// <summary>临时目录.</summary>
        public readonly string TempDirectory;

        /// <summary>连接管理器.</summary>
        public readonly DuckDbConnectionManager Manager;

        /// <summary>批量写入器.</summary>
        public readonly IngestWriterService Writer;

        /// <summary>Schema 引导.</summary>
        public readonly DuckDbSchemaBootstrapper Bootstrapper;

        /// <summary>rollup 服务.</summary>
        public readonly RollupService Service;

        /// <summary>指标存储.</summary>
        public readonly MetricStore MetricStore;

        /// <summary>取消令牌.</summary>
        public readonly CancellationToken Token = CancellationToken.None;

        /// <summary>
        /// Initializes a new instance of the <see cref="RollupHarness"/> class.
        /// </summary>
        public RollupHarness()
        {
            TempDirectory = Path.Combine(Path.GetTempPath(), "sdata-rollup-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(TempDirectory);

            var options = new SystemOptions();
            options.DuckDb.Path = Path.Combine(TempDirectory, "rollup-test.duckdb");
            options.DuckDb.Threads = 2;

            Manager = new DuckDbConnectionManager(options, NullLogger<DuckDbConnectionManager>.Instance);
            Manager.InitializeAsync().GetAwaiter().GetResult();
            Bootstrapper = new DuckDbSchemaBootstrapper(Manager, NullLogger<DuckDbSchemaBootstrapper>.Instance);
            Bootstrapper.ApplyAsync().GetAwaiter().GetResult();
            Writer = new IngestWriterService(new IngestBuffer(options), Manager, options, new SData.Infra.Services.DefaultIdProvider(1), NullLogger<IngestWriterService>.Instance);
            Service = new RollupService(Manager, NullLogger<RollupService>.Instance);
            MetricStore = new MetricStore(Manager, options);
        }

        /// <summary>
        /// 取一个必然落在 1h「已关闭回填窗口」[floor1h-4h, floor1h) 内的时间戳.
        /// </summary>
        /// <returns>时间戳.</returns>
        public static DateTimeOffset ClosedBucketTime()
        {
            var now = DateTimeOffset.UtcNow;
            var floorHour = new DateTimeOffset(now.UtcDateTime.Ticks - (now.UtcDateTime.Ticks % TimeSpan.FromHours(1).Ticks), TimeSpan.Zero);
            return floorHour.AddMinutes(-90);
        }

        /// <summary>
        /// 取一个必然落在 5m「已关闭回填窗口」[floor5m-60m, floor5m) 内的时间戳.
        /// </summary>
        /// <returns>时间戳.</returns>
        public static DateTimeOffset Closed5mBucketTime()
        {
            var now = DateTimeOffset.UtcNow;
            var floor5m = new DateTimeOffset(now.UtcDateTime.Ticks - (now.UtcDateTime.Ticks % TimeSpan.FromMinutes(5).Ticks), TimeSpan.Zero);
            return floor5m.AddMinutes(-30);
        }

        /// <summary>
        /// 读 1h rollup 表中某指标的（行数, sum 合计, count 合计）.
        /// </summary>
        /// <param name="name">指标名.</param>
        /// <returns>三元组.</returns>
        public async Task<(int Rows, double Sum, long Count)> Read1hAsync(string name)
        {
            return await Manager.QueryAsync(
                $"SELECT count(*), coalesce(sum(\"sum\"), 0), coalesce(sum(\"count\"), 0) FROM {TimeTables.Rollup1hTable} WHERE name = $name",
                [new DuckDB.NET.Data.DuckDBParameter("name", name)],
                async reader =>
                {
                    await reader.ReadAsync(Token);
                    return (reader.GetInt32(0), reader.GetDouble(1), reader.GetInt64(2));
                }, Token);
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            Manager.DisposeAsync().AsTask().GetAwaiter().GetResult();
            try
            {
                Directory.Delete(TempDirectory, recursive: true);
            }
            catch (IOException)
            {
                // 临时目录清理失败不阻塞测试
            }
        }
    }
}
