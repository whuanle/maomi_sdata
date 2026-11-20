using MediatR;
using SData.Infra.Models;
using SData.Infra.Services;

namespace SData.Rum.Models;

/// <summary>
/// RUM 项目项.
/// </summary>
public class RumProjectItem
{
    /// <summary>id.</summary>
    public long Id { get; set; }

    /// <summary>项目标识（C3 上报凭证）.</summary>
    public string ProjectKey { get; set; } = string.Empty;

    /// <summary>项目名称.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>允许上报域名（JSON 数组字符串）.</summary>
    public string DomainsJson { get; set; } = "[]";

    /// <summary>性能事件采样率（0–1）.</summary>
    public double PerfSampleRate { get; set; }

    /// <summary>会话回放采样率（0–1）.</summary>
    public double ReplaySampleRate { get; set; }

    /// <summary>创建时间.</summary>
    public DateTimeOffset CreateTime { get; set; }

    /// <summary>更新时间.</summary>
    public DateTimeOffset UpdateTime { get; set; }
}

/// <summary>
/// 保存 RUM 项目（新增/修改）.
/// </summary>
public class SaveRumProjectCommand : IRequest<Simple<long>>, IUserIdContext, IModelValidator<SaveRumProjectCommand>
{
    /// <summary>id（0 = 新增）.</summary>
    public long Id { get; set; }

    /// <summary>项目标识（新增必填，形如 rum_xxx；修改不可变）.</summary>
    public string ProjectKey { get; set; } = string.Empty;

    /// <summary>项目名称.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>允许上报域名（JSON 数组字符串）.</summary>
    public string DomainsJson { get; set; } = "[]";

    /// <summary>性能事件采样率（0–1）.</summary>
    public double PerfSampleRate { get; set; } = 1.0;

    /// <summary>会话回放采样率（0–1）.</summary>
    public double ReplaySampleRate { get; set; } = 1.0;

    /// <inheritdoc/>
    public long ContextUserId { get; set; }

    /// <inheritdoc/>
    public string ContextUserRole { get; set; } = string.Empty;

    /// <inheritdoc/>
    public static void Validate(AbstractValidator<SaveRumProjectCommand> validate)
    {
        validate.RuleFor(x => x.ProjectKey).NotEmpty().Matches("^rum_[a-zA-Z0-9_\\-]{1,32}$");
        validate.RuleFor(x => x.Name).NotEmpty().MaximumLength(64);
        validate.RuleFor(x => x.DomainsJson).Must(json => RumOrigins.TryParse(json, out _))
            .WithMessage("允许站点必须是 HTTP/HTTPS Origin 的 JSON 字符串数组");
        validate.RuleFor(x => x.PerfSampleRate).InclusiveBetween(0, 1);
        validate.RuleFor(x => x.ReplaySampleRate).InclusiveBetween(0, 1);
    }
}

/// <summary>
/// 删除 RUM 项目.
/// </summary>
public class DeleteRumProjectCommand : IRequest<EmptyCommandResponse>, IUserIdContext
{
    /// <summary>项目 id.</summary>
    public long Id { get; set; }

    /// <inheritdoc/>
    public long ContextUserId { get; set; }

    /// <inheritdoc/>
    public string ContextUserRole { get; set; } = string.Empty;
}

/// <summary>
/// RUM 项目列表查询.
/// </summary>
public class QueryRumProjectsCommand : PagedParameter, IRequest<PagedResult<RumProjectItem>>, IModelValidator<QueryRumProjectsCommand>
{
    /// <summary>关键字（项目标识/名称模糊）.</summary>
    public string? Search { get; set; }

    /// <inheritdoc/>
    public static void Validate(AbstractValidator<QueryRumProjectsCommand> validate)
    {
    }
}
