using DuckDB.NET.Data;
using Microsoft.Extensions.Logging.Abstractions;
using SData.Infra.Configuration;
using SData.Store.Core;
using Xunit;

namespace SData.Store.Tests;

public class DuckDbMemorySettingsTests
{
    [Fact]
    public async Task RuntimeLimit_IsVisibleToExistingAndNewConnections()
    {
        var directory = Path.Combine(Path.GetTempPath(), "sdata-memory-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var options = new SystemOptions();
        options.DuckDb.Path = Path.Combine(directory, "memory.duckdb");
        var manager = new DuckDbConnectionManager(options, NullLogger<DuckDbConnectionManager>.Instance);
        try
        {
            await manager.InitializeAsync();
            using var existing = manager.CloneConnection();
            var old = await ReadLimitAsync(existing);
            await manager.ApplyMemoryLimitAsync("3GB", default);
            Assert.NotEqual(old, manager.EffectiveMemoryLimit);
            Assert.Equal(manager.EffectiveMemoryLimit, await ReadLimitAsync(existing));
            using var fresh = manager.CloneConnection();
            Assert.Equal(manager.EffectiveMemoryLimit, await ReadLimitAsync(fresh));
            await manager.ApplyMemoryLimitAsync("256MiB", default);
            Assert.Equal("256.0 MiB", await ReadLimitAsync(existing));
            await Assert.ThrowsAsync<ArgumentException>(() => manager.ApplyMemoryLimitAsync("2GB'; SET threads=99; --", default));
            Assert.Equal("256MiB", manager.MemoryLimit);
            using var command = fresh.CreateCommand();
            command.CommandText = "SELECT current_setting('threads')";
            Assert.Equal(options.DuckDb.Threads, Convert.ToInt32(await command.ExecuteScalarAsync()));
        }
        finally
        {
            await manager.DisposeAsync();
            Directory.Delete(directory, true);
        }
    }

    private static async Task<string> ReadLimitAsync(DuckDBConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT current_setting('memory_limit')";
        return (string)(await command.ExecuteScalarAsync())!;
    }
}
