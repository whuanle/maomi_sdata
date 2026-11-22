using Yitter.IdGenerator;

namespace SData.Infra.Services;

/// <summary>
/// 默认 id 生成器（Yitter 雪花算法）.
/// </summary>
public class DefaultIdProvider : IIdProvider
{
    private readonly object _lock = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="DefaultIdProvider"/> class.
    /// </summary>
    /// <param name="workId">机器 id.</param>
    public DefaultIdProvider(ushort workId)
    {
        YitIdHelper.SetIdGenerator(new IdGeneratorOptions(workId));
    }

    /// <inheritdoc/>
    public long GetLongId()
    {
        lock (_lock)
        {
            return YitIdHelper.NextId();
        }
    }
}
