using MediatR;
using SData.Infra.Services;
using SData.Store.QueryDsl;

namespace SData.Iot;

/// <summary>
/// 遥测统一查询（契约 C1，POST /api/iot/search）：请求体即 DSL 模型，响应为列式结构.
/// </summary>
public class SearchTelemetryCommand : QueryDslModel, IRequest<SearchDslResponse>, IModelValidator<SearchTelemetryCommand>
{
    /// <inheritdoc/>
    public static void Validate(AbstractValidator<SearchTelemetryCommand> validate)
    {
        validate.RuleFor(x => x.Limit).InclusiveBetween(1, 10000).When(x => x.Limit.HasValue);
    }
}
