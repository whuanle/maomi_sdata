using DuckDB.NET.Data;
using global::Xunit;

namespace SData.Store.Tests;

/// <summary>真实原生查询取消验证：中断必须终止数据库工作，而非仅停止等待.</summary>
public class DuckDbCancellationTests
{
    [Fact]
    public async Task Cancel_InterruptsNativeQuery_AndConnectionRemainsUsable()
    {
        using var connection = new DuckDBConnection("Data Source=:memory:");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT sum(sin(a.range + b.range)) FROM range(1000000) a CROSS JOIN range(1000000) b";
        using var started = new ManualResetEventSlim();
        var query = Task.Run(() =>
        {
            started.Set();
            return Record.Exception(() => command.ExecuteScalar());
        });
        Assert.True(started.Wait(TimeSpan.FromSeconds(5)));
        // 周期中断覆盖 Cancel 早于原生查询进入执行阶段的竞争窗口.
        using var timer = new Timer(_ => command.Cancel(), null, 100, 100);
        var failure = await query.WaitAsync(TimeSpan.FromSeconds(10));
        await timer.DisposeAsync();
        Assert.NotNull(failure);
        Assert.IsAssignableFrom<OperationCanceledException>(failure);
        command.CommandText = "SELECT 42";
        Assert.Equal(42, Convert.ToInt32(command.ExecuteScalar()));
    }
}
