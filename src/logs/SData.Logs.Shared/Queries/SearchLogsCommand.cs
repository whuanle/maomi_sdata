using MediatR;
using SData.Infra.Services;
using SData.Store.QueryDsl;

namespace SData.Logs;

/// <summary>
/// 日志统一查询（契约 C1，POST /api/logs/search）：请求体即 DSL 模型，响应为列式结构.
/// </summary>
public class SearchLogsCommand : QueryDslModel, IRequest<SearchDslResponse>, IModelValidator<SearchLogsCommand>
{
    /// <inheritdoc/>
    public static void Validate(AbstractValidator<SearchLogsCommand> validate)
    {
        validate.RuleFor(x => x.Limit).InclusiveBetween(1, 10000).When(x => x.Limit.HasValue);
    }
}
