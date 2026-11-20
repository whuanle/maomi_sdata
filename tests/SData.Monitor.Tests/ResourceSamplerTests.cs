using global::Xunit;
using Microsoft.Extensions.Logging.Abstractions;

namespace SData.Monitor.Tests;

public class ResourceSamplerTests
{
    [Theory]
    [InlineData(20, 100, 20)]
    [InlineData(200, 100, 100)]
    public void CpuDifference_IsNormalized(double used, double total, double expected)
        => Assert.Equal(expected, ResourceSampler.CpuPercent(used, total));

    [Fact]
    public void InvalidCpuWindow_IsUnknown()
    {
        Assert.Null(ResourceSampler.CpuPercent(0, 0));
        Assert.Null(ResourceSampler.CpuPercent(-1, 100));
    }

    [Fact]
    public void LinuxCpu_ExcludesDuplicatedGuestCounters()
    {
        var result = ResourceSampler.ParseLinuxCpu("cpu  10 20 30 40 5 6 7 8 100 200");
        Assert.Equal(126UL, result.Total);
        Assert.Equal(45UL, result.Idle);
    }

    [Fact]
    public void LinuxMemory_UsesAvailableNotOnlyFree()
    {
        var result = ResourceSampler.ParseLinuxMemory(["MemTotal: 1000 kB", "MemFree: 100 kB", "MemAvailable: 400 kB"]);
        Assert.Equal(1024000, result.Total);
        Assert.Equal(409600, result.Available);
    }

    [Theory]
    [InlineData("0::/app", "cgroup2", "")]
    [InlineData("5:memory,cpu:/app", "cgroup", "memory,cpu")]
    public void ContainerMetrics_ResolveMountedProcessGroup(string group, string fileSystem, string controllers)
    {
        var mount = $"1 0 0:1 / /cgroup rw - {fileSystem} cgroup rw,{controllers}";
        var values = new Dictionary<string, string>
        {
            ["/cgroup/app/memory.max"] = "1024",
            ["/cgroup/app/memory.current"] = "512",
            ["/cgroup/app/cpu.max"] = "50000 100000",
        };
        var result = ContainerResources.Parse(group, mount, path => values.GetValueOrDefault(path.Replace('\\', '/')));
        Assert.Equal(1024, result.MemoryLimit);
        Assert.Equal(512, result.MemoryUsed);
        Assert.Equal(0.5, result.CpuLimit);
    }

    [Fact]
    public void ContainerMetrics_UnlimitedAndMissingAreUnknown()
    {
        var result = ContainerResources.Parse("0::/", "1 0 0:1 / /cgroup rw - cgroup2 cgroup rw",
            path => path.EndsWith("memory.max", StringComparison.Ordinal) ? "max" : null);
        Assert.Null(result.MemoryLimit);
        Assert.Null(result.CpuLimit);
        Assert.Null(result.MemoryUsed);
    }

    [Fact]
    public async Task Sampler_PublishesFirstSampleWithoutInventingCpuValue()
    {
        using var sampler = new ResourceSampler(NullLogger<ResourceSampler>.Instance);
        await sampler.StartAsync(CancellationToken.None);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (sampler.Snapshot.SampledAt == null)
            await Task.Delay(10, deadline.Token);
        var snapshot = sampler.Snapshot;
        Assert.True(snapshot.ProcessorCount > 0);
        Assert.NotEmpty(snapshot.Os);
        Assert.True(snapshot.WorkingSetBytes > 0);
        Assert.True(snapshot.ProcessThreads > 0);
        Assert.Null(snapshot.ProcessCpuPercent);
        Assert.Null(snapshot.SystemCpuPercent);
        await sampler.StopAsync(CancellationToken.None);
    }
}
