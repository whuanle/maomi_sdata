namespace SData.Database.Audits;

/// <summary>
/// 创建审计.
/// </summary>
public interface ICreationAudited
{
    /// <summary>
    /// 创建人 id.
    /// </summary>
    long CreateUserId { get; set; }

    /// <summary>
    /// 创建时间.
    /// </summary>
    DateTimeOffset CreateTime { get; set; }
}

/// <summary>
/// 修改审计.
/// </summary>
public interface IModificationAudited
{
    /// <summary>
    /// 修改人 id.
    /// </summary>
    long UpdateUserId { get; set; }

    /// <summary>
    /// 修改时间.
    /// </summary>
    DateTimeOffset UpdateTime { get; set; }
}

/// <summary>
/// 软删除.
/// </summary>
public interface IDeleteAudited
{
    /// <summary>
    /// 0 = 正常；非 0 = 删除时间 ticks.
    /// </summary>
    long IsDeleted { get; set; }
}

/// <summary>
/// 完整审计.
/// </summary>
public interface IFullAudited : ICreationAudited, IModificationAudited, IDeleteAudited
{
}

/// <summary>
/// 完整审计基类.
/// </summary>
public abstract class FullAuditedEntity : IFullAudited
{
    /// <inheritdoc/>
    public long CreateUserId { get; set; }

    /// <inheritdoc/>
    public DateTimeOffset CreateTime { get; set; }

    /// <inheritdoc/>
    public long UpdateUserId { get; set; }

    /// <inheritdoc/>
    public DateTimeOffset UpdateTime { get; set; }

    /// <inheritdoc/>
    public long IsDeleted { get; set; }
}
