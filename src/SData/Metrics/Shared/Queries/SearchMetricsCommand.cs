using Mediator;
using SData.Infra.Services;
using SData.Store.QueryDsl;

namespace SData.Metrics;

/// <summary>
/// 指标统一查询（契约 C1，POST /api/metrics/search）：请求体即 DSL 模型，响应为列式结构.
/// </summary>
public class SearchMetricsCommand : QueryDslModel, IRequest<SearchDslResponse>, IModelValidator<SearchMetricsCommand>
{
    /// <inheritdoc/>
    public static void Validate(AbstractValidator<SearchMetricsCommand> validate)
    {
        validate.RuleFor(x => x.Limit).InclusiveBetween(1, 10000).When(x => x.Limit.HasValue);
    }
}
