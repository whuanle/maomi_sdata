using global::Xunit;

namespace SData.Monitor.Tests;

public class MacSystemResourcesTests
{
    [Fact]
    public void CpuCounters_UseIdleSlotAndAvoid32BitOverflow()
    {
        var counters = MacSystemResources.SumCpu([uint.MaxValue, 20, 30, 40]);
        Assert.Equal((ulong)uint.MaxValue + 90, counters.Total);
        Assert.Equal(30UL, counters.Idle);
    }

    [Theory]
    [InlineData(4096)]
    [InlineData(16384)]
    public void MemoryEstimate_UsesRuntimePageSize(uint pageSize)
    {
        Assert.Equal(30L * pageSize, MacSystemResources.AvailableMemory(10, 20, pageSize, long.MaxValue));
        Assert.Equal(100, MacSystemResources.AvailableMemory(10, 20, pageSize, 100));
    }

    private sealed class MacFactAttribute : FactAttribute
    {
        public MacFactAttribute()
        {
            if (!OperatingSystem.IsMacOS())
            {
                Skip = "Requires macOS to exercise Mach/libSystem";
            }
        }
    }

    [MacFact]
    public void NativeCounters_OnMacOS()
    {
        var snapshot = MacSystemResources.Read();
        Assert.True(snapshot.Total >= snapshot.Idle);
        Assert.True(snapshot.TotalMemory > 0);
        Assert.InRange(snapshot.AvailableMemory, 0, snapshot.TotalMemory);
    }
}
