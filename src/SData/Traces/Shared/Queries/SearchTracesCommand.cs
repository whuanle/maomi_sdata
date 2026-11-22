using Mediator;
using SData.Infra.Services;
using SData.Store.QueryDsl;

namespace SData.Traces;

/// <summary>
/// 链路统一查询（契约 C1，POST /api/traces/search）：请求体即 DSL 模型，响应为列式结构.
/// </summary>
public class SearchTracesCommand : QueryDslModel, IRequest<SearchDslResponse>, IModelValidator<SearchTracesCommand>
{
    /// <inheritdoc/>
    public static void Validate(AbstractValidator<SearchTracesCommand> validate)
    {
        validate.RuleFor(x => x.Limit).InclusiveBetween(1, 10000).When(x => x.Limit.HasValue);
    }
}
