namespace SData.Infra.Services;

/// <summary>
/// id 生成器（雪花 id）.
/// </summary>
public interface IIdProvider
{
    /// <summary>
    /// 生成long id.
    /// </summary>
    /// <returns>雪花 id.</returns>
    long GetLongId();
}
