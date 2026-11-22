using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace SData.Monitor;

/// <summary>后台差分采样，不在请求中等待 CPU 采样窗口.</summary>
public sealed class ResourceSampler(ILogger<ResourceSampler> logger) : BackgroundService
{
    private ResourceSnapshot _snapshot = new();
    private (ulong Total, ulong Idle)? _systemBaseline;
    private TimeSpan? _processBaseline;
    private long _timestamp;

    public ResourceSnapshot Snapshot => Volatile.Read(ref _snapshot);

    public static double? CpuPercent(double used, double total)
        => total > 0 && used >= 0 ? Math.Clamp(used / total * 100, 0, 100) : null;

    public static (ulong Total, ulong Idle) ParseLinuxCpu(string line)
    {
        var values = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Skip(1).Take(8).Select(x => ulong.Parse(x, CultureInfo.InvariantCulture)).ToArray();
        if (values.Length < 4) throw new FormatException("Invalid CPU counters");
        return (values.Aggregate(0UL, (sum, value) => sum + value), values[3] + (values.Length > 4 ? values[4] : 0));
    }

    public static (long Total, long Available) ParseLinuxMemory(IEnumerable<string> lines)
    {
        var fields = lines.Select(x => x.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .Where(x => x.Length >= 2).ToDictionary(x => x[0].TrimEnd(':'), x => long.Parse(x[1], CultureInfo.InvariantCulture) * 1024);
        return (fields["MemTotal"], fields["MemAvailable"]);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        do
        {
            try { Sample(); }
            catch (Exception exception)
            {
                logger.LogWarning("Resource sampling failed ({ErrorType})", exception.GetType().Name);
                Volatile.Write(ref _snapshot, Snapshot with { State = "error" });
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private void Sample()
    {
        using var process = Process.GetCurrentProcess();
        var now = Stopwatch.GetTimestamp();
        var processTime = process.TotalProcessorTime;
        var container = ContainerResources.Read();
        var cpuLimit = container.CpuLimit;
        var capacity = Math.Min(Environment.ProcessorCount, cpuLimit ?? Environment.ProcessorCount);
        var processCpu = _processBaseline.HasValue
            ? CpuPercent((processTime - _processBaseline.Value).TotalSeconds, Stopwatch.GetElapsedTime(_timestamp, now).TotalSeconds * capacity)
            : null;
        _processBaseline = processTime;
        _timestamp = now;

        (ulong Total, ulong Idle)? system = null;
        long? totalMemory = null;
        long? availableMemory = null;
        var state = "ok";
        try
        {
            if (OperatingSystem.IsWindows())
            {
                if (!GetSystemTimes(out var idle, out var kernel, out var user)) throw new InvalidOperationException();
                system = (kernel + user, idle);
                var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
                if (!GlobalMemoryStatusEx(ref memory)) throw new InvalidOperationException();
                totalMemory = checked((long)memory.TotalPhysical);
                availableMemory = checked((long)memory.AvailablePhysical);
            }
            else if (OperatingSystem.IsLinux())
            {
                system = ParseLinuxCpu(File.ReadLines("/proc/stat").First());
                var memory = ParseLinuxMemory(File.ReadLines("/proc/meminfo"));
                totalMemory = memory.Total;
                availableMemory = memory.Available;
            }
            else if (OperatingSystem.IsMacOS())
            {
                var mac = MacSystemResources.Read();
                system = (mac.Total, mac.Idle);
                totalMemory = mac.TotalMemory;
                availableMemory = mac.AvailableMemory;
            }
            else state = "unsupported";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or FormatException or InvalidOperationException)
        {
            state = "partial";
        }

        double? systemCpu = null;
        if (system.HasValue && _systemBaseline.HasValue && system.Value.Total >= _systemBaseline.Value.Total && system.Value.Idle >= _systemBaseline.Value.Idle)
        {
            var total = system.Value.Total - _systemBaseline.Value.Total;
            var idle = system.Value.Idle - _systemBaseline.Value.Idle;
            systemCpu = CpuPercent((double)total - idle, total);
        }

        _systemBaseline = system;
        Volatile.Write(ref _snapshot, new ResourceSnapshot
        {
            State = state, SampledAt = DateTimeOffset.UtcNow,
            Os = RuntimeInformation.OSDescription, OsArchitecture = RuntimeInformation.OSArchitecture.ToString(),
            ProcessArchitecture = RuntimeInformation.ProcessArchitecture.ToString(), Runtime = RuntimeInformation.FrameworkDescription,
            ProcessorCount = Environment.ProcessorCount, SystemCpuPercent = systemCpu, ProcessCpuPercent = processCpu,
            TotalMemoryBytes = totalMemory, AvailableMemoryBytes = availableMemory,
            WorkingSetBytes = process.WorkingSet64, PrivateMemoryBytes = process.PrivateMemorySize64,
            GcHeapBytes = GC.GetTotalMemory(false),
            GcCollectionCount0 = GC.CollectionCount(0), GcCollectionCount1 = GC.CollectionCount(1), GcCollectionCount2 = GC.CollectionCount(2),
            ProcessThreads = process.Threads.Count,
            ThreadPoolThreads = ThreadPool.ThreadCount, PendingWorkItems = ThreadPool.PendingWorkItemCount,
            ContainerCpuLimit = cpuLimit,
            ContainerMemoryLimitBytes = container.MemoryLimit,
            ContainerMemoryBytes = container.MemoryUsed,
        });
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out ulong idle, out ulong kernel, out ulong user);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus memory);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }
}
