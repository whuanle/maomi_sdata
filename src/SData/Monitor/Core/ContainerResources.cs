using System.Globalization;

namespace SData.Monitor;

/// <summary>只读取当前进程可见的 cgroup 挂载；不要求宿主目录或特权权限.</summary>
public static class ContainerResources
{
    public sealed record Values(long? MemoryLimit, long? MemoryUsed, double? CpuLimit);

    public static Values Read()
    {
        if (!OperatingSystem.IsLinux()) return new(null, null, null);
        return Parse(SafeRead("/proc/self/cgroup"), SafeRead("/proc/self/mountinfo"), SafeRead);
    }

    public static Values Parse(string? groups, string? mounts, Func<string, string?> read)
    {
        var entries = (groups ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Split(':', 3)).Where(x => x.Length == 3).ToArray();
        var mountEntries = (mounts ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        string? Resolve(string controller)
        {
            foreach (var mount in mountEntries)
            {
                var halves = mount.Split(" - ", 2);
                if (halves.Length != 2) continue;
                var left = halves[0].Split(' ');
                var right = halves[1].Split(' ');
                if (left.Length < 5 || right.Length < 3) continue;
                var unified = right[0] == "cgroup2";
                if (!unified && (right[0] != "cgroup" || !right[2].Split(',').Contains(controller))) continue;
                var group = entries.FirstOrDefault(x => unified ? x[1] == string.Empty : x[1].Split(',').Contains(controller));
                if (group == null) continue;
                var root = Unescape(left[3]).TrimEnd('/');
                var member = group[2];
                // cgroup namespaces may report "/" relative to the mounted subtree.
                var relative = member == "/" ? string.Empty
                    : root.Length == 0 ? member.TrimStart('/')
                    : member == root ? string.Empty
                    : member.StartsWith(root + "/", StringComparison.Ordinal) ? member[(root.Length + 1)..] : null;
                if (relative == null || relative.Split('/').Contains("..")) continue;
                return Path.Combine(Unescape(left[4]), relative);
            }

            return null;
        }

        var memory = Resolve("memory");
        var cpu = Resolve("cpu");
        string? ReadAt(string? directory, string name) => directory == null ? null : read(Path.Combine(directory, name))?.Trim();
        var limit = Number(ReadAt(memory, "memory.max")) ?? Number(ReadAt(memory, "memory.limit_in_bytes"));
        if (limit >= 1L << 60) limit = null; // v1 unlimited sentinel, not real RAM capacity
        var used = Number(ReadAt(memory, "memory.current")) ?? Number(ReadAt(memory, "memory.usage_in_bytes"));
        var parts = ReadAt(cpu, "cpu.max")?.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var quota = parts is { Length: 2 } ? Number(parts[0]) : Number(ReadAt(cpu, "cpu.cfs_quota_us"));
        var period = parts is { Length: 2 } ? Number(parts[1]) : Number(ReadAt(cpu, "cpu.cfs_period_us"));
        return new(limit, used, quota > 0 && period > 0 ? (double)quota.Value / period.Value : null);
    }

    private static long? Number(string? value)
        => long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) && result >= 0 ? result : null;

    private static string Unescape(string path)
        => path.Replace("\\040", " ", StringComparison.Ordinal).Replace("\\011", "\t", StringComparison.Ordinal).Replace("\\134", "\\", StringComparison.Ordinal);

    private static string? SafeRead(string path)
    {
        try { return File.ReadAllText(path); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return null; }
    }
}
