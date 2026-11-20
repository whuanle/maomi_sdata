using System.Diagnostics;
using DuckDB.NET.Data;
using Xunit;
using Xunit.Abstractions;

namespace SData.Store.Tests;

/// <summary>驱动结果物化与高频索引删插的独立对照实验，不访问业务库.</summary>
public class DuckDbMemoryRootCauseTests(ITestOutputHelper output)
{
    [Fact]
    public void MaterializedVersusBoundedStreaming_ReportsNativeMemoryAndReleasesResults()
    {
        using var connection = new DuckDBConnection("Data Source=:memory:");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SET memory_limit='128MB'; SET threads=2; SET preserve_insertion_order=false";
        command.ExecuteNonQuery();
        foreach (var bounded in new[] { false, true })
        {
            command.UseStreamingMode = bounded;
            command.CommandText = "SELECT i, repeat(md5(i::VARCHAR),32) AS payload FROM range(100000) r(i)" + (bounded ? " LIMIT 3" : "");
            var before = Process.GetCurrentProcess().PrivateMemorySize64;
            using (var reader = command.ExecuteReader())
            {
                Assert.True(reader.Read());
                Assert.Equal(1024, reader.GetString(1).Length);
                var during = Process.GetCurrentProcess().PrivateMemorySize64;
                output.WriteLine($"bounded={bounded}, before={before}, result_open={during}");
            }
            command.CommandText = "SELECT 42";
            Assert.Equal(42, Convert.ToInt32(command.ExecuteScalar()));
            output.WriteLine($"bounded={bounded}, disposed={Process.GetCurrentProcess().PrivateMemorySize64}");
        }
    }

    [Fact]
    public void IndexedVersusUnindexedBucketReplacement_ReproducesWriteAmplification()
    {
        var directory = Path.Combine(Path.GetTempPath(), "sdata-rollup-contrast-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var sizes = new Dictionary<bool, long>();
        try
        {
            foreach (var indexed in new[] { false, true })
            {
                var path = Path.Combine(directory, indexed ? "indexed.duckdb" : "plain.duckdb");
                using (var connection = new DuckDBConnection("Data Source=" + path))
                {
                    connection.Open();
                    using var command = connection.CreateCommand();
                    void Execute(string sql) { command.CommandText = sql; command.ExecuteNonQuery(); }
                    Execute("SET memory_limit='128MB'; SET threads=2; SET preserve_insertion_order=false");
                    Execute("CREATE TABLE buckets(name VARCHAR, labels_hash VARCHAR, bucket_ts TIMESTAMP, value DOUBLE)");
                    if (indexed) Execute("CREATE INDEX bucket_idx ON buckets(name, labels_hash, bucket_ts)");
                    for (var iteration = 0; iteration < 30; iteration++)
                    {
                        Execute("DELETE FROM buckets");
                        Execute("INSERT INTO buckets SELECT 'metric', md5(i::VARCHAR), TIMESTAMP '2026-11-20', i::DOUBLE FROM range(5000) r(i)");
                        Execute("CHECKPOINT");
                    }
                    command.CommandText = "SELECT count(*) FROM buckets";
                    Assert.Equal(5000, Convert.ToInt64(command.ExecuteScalar()));
                }
                sizes[indexed] = new FileInfo(path).Length;
                output.WriteLine($"indexed={indexed}, bytes={sizes[indexed]}");
            }
            Assert.True(sizes[true] > sizes[false] * 2, "复合ART索引反复删插应复现明显写放大，对照失效时重新评估修复依据");
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }
}
