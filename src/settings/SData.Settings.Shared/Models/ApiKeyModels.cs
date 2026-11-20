using MediatR;
using SData.Infra.Models;
using SData.Infra.Services;

namespace SData.Settings;

/// <summary>不含哈希和秘密的凭据列表项.</summary>
public record ApiKeyItem(string Id, string Kind, string Name, string Prefix, bool Enabled, bool CanReveal,
    DateTimeOffset CreateTime, string CreateUserId, DateTimeOffset? UpdateTime, string UpdateUserId);
public record ApiKeySecretResponse(ApiKeyItem Item, string Secret);

public class QueryApiKeysCommand : IRequest<List<ApiKeyItem>>, IModelValidator<QueryApiKeysCommand>
{
    public static void Validate(AbstractValidator<QueryApiKeysCommand> validate) { }
}

/// <summary>统一凭据管理动作，秘密只在创建、重置和显式查看时返回.</summary>
public class ManageApiKeyCommand : IRequest<ApiKeySecretResponse>, IUserIdContext, IModelValidator<ManageApiKeyCommand>
{
    public string Kind { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string? Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool Enabled { get; set; }
    public long ContextUserId { get; set; }
    public string ContextUserRole { get; set; } = string.Empty;

    public static void Validate(AbstractValidator<ManageApiKeyCommand> validate)
    {
        validate.RuleFor(x => x.Kind).Must(x => x is "ingestion" or "agent");
        validate.RuleFor(x => x.Action).Must(x => x is "create" or "rotate" or "enable" or "delete" or "reveal");
        validate.RuleFor(x => x.Name).NotNull().MaximumLength(64);
        validate.RuleFor(x => x.Name).NotEmpty().When(x => x.Action == "create");
        validate.RuleFor(x => x.Id).Must(x => long.TryParse(x, out var id) && id > 0).When(x => x.Action != "create");
    }
}
