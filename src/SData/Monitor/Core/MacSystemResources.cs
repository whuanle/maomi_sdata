using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace SData.Monitor;

/// <summary>macOS 系统计数器；使用 libSystem/Mach，不启动 shell 或依赖命令输出.</summary>
public static class MacSystemResources
{
    private const string LibSystem = "/usr/lib/libSystem.B.dylib";
    private const int HostCpuLoadInfo = 3;
    private const int HostVmInfo64 = 4;
    private static readonly Lazy<uint> TaskPort = new(ReadTaskPort);

    [SupportedOSPlatform("macos")]
    public static (ulong Total, ulong Idle, long TotalMemory, long AvailableMemory) Read()
    {
        var host = MachHostSelf();
        if (host == 0)
        {
            throw new InvalidOperationException("Mach host port unavailable");
        }

        try
        {
            var cpu = new uint[4];
            uint cpuCount = (uint)cpu.Length;
            if (HostStatistics(host, HostCpuLoadInfo, cpu, ref cpuCount) != 0 || cpuCount < 4)
            {
                throw new InvalidOperationException("Mach CPU counters unavailable");
            }

            // vm_statistics64 首四项为 free/active/inactive/wired 页数。
            // 预留缓冲兼容不同 macOS 版本，避免依赖后续字段的结构体对齐。
            var vm = new uint[64];
            uint vmCount = (uint)vm.Length;
            nuint pageSize = 0;
            if (HostStatistics64(host, HostVmInfo64, vm, ref vmCount) != 0 || vmCount < 4
                || HostPageSize(host, ref pageSize) != 0 || pageSize == 0)
            {
                throw new InvalidOperationException("Mach memory counters unavailable");
            }

            ulong totalMemory = 0;
            nuint length = sizeof(ulong);
            if (SysctlByName("hw.memsize", ref totalMemory, ref length, IntPtr.Zero, 0) != 0)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            var cpuTimes = SumCpu(cpu);
            return (cpuTimes.Total, cpuTimes.Idle, checked((long)totalMemory),
                AvailableMemory(vm[0], vm[2], checked((uint)pageSize), checked((long)totalMemory)));
        }
        finally
        {
            // mach_host_self 返回一个 send right，每次采样后释放，防止长期运行泄漏端口引用。
            _ = MachPortDeallocate(TaskPort.Value, host);
        }
    }

    public static (ulong Total, ulong Idle) SumCpu(IReadOnlyList<uint> ticks)
    {
        if (ticks.Count < 4)
        {
            throw new ArgumentException("Four CPU counters are required", nameof(ticks));
        }

        return ((ulong)ticks[0] + ticks[1] + ticks[2] + ticks[3], ticks[2]);
    }

    /// <summary>可用内存估计为 free + inactive，不重复累加 speculative/purgeable 页.</summary>
    public static long AvailableMemory(uint freePages, uint inactivePages, uint pageSize, long totalBytes)
        => Math.Min(totalBytes, checked((long)(((ulong)freePages + inactivePages) * pageSize)));

    [DllImport(LibSystem, EntryPoint = "mach_host_self")]
    private static extern uint MachHostSelf();

    private static uint ReadTaskPort()
    {
        var library = NativeLibrary.Load(LibSystem);
        try
        {
            // mach_task_self() 在 SDK 中是访问全局端口的宏，不依赖其是否导出为函数。
            return unchecked((uint)Marshal.ReadInt32(NativeLibrary.GetExport(library, "mach_task_self_")));
        }
        finally
        {
            NativeLibrary.Free(library);
        }
    }

    [DllImport(LibSystem, EntryPoint = "mach_port_deallocate")]
    private static extern int MachPortDeallocate(uint task, uint name);

    [DllImport(LibSystem, EntryPoint = "host_statistics")]
    private static extern int HostStatistics(uint host, int flavor, [Out] uint[] info, ref uint count);

    [DllImport(LibSystem, EntryPoint = "host_statistics64")]
    private static extern int HostStatistics64(uint host, int flavor, [Out] uint[] info, ref uint count);

    [DllImport(LibSystem, EntryPoint = "host_page_size")]
    private static extern int HostPageSize(uint host, ref nuint pageSize);

    [DllImport(LibSystem, EntryPoint = "sysctlbyname", SetLastError = true)]
    private static extern int SysctlByName([MarshalAs(UnmanagedType.LPUTF8Str)] string name, ref ulong value, ref nuint length, IntPtr newValue, nuint newLength);
}
