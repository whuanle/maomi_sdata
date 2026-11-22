using System.Security.Cryptography;
using Mediator;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SData.Database;
using SData.Database.Entities;
using SData.Infra.Exceptions;
using SData.Infra.Services;
using SData.Settings.Services;

namespace SData.Settings.Handlers;

/// <summary>凭据按用途隔离，永不返回数据库哈希.</summary>
public class QueryApiKeysCommandHandler(DatabaseContext database) : IRequestHandler<QueryApiKeysCommand, List<ApiKeyItem>>
{
    public async ValueTask<List<ApiKeyItem>> Handle(QueryApiKeysCommand request, CancellationToken cancellationToken)
    {
        var ingestion = await database.IngestKeys.AsNoTracking().ToListAsync(cancellationToken);
        var agents = await database.AgentKeys.AsNoTracking().ToListAsync(cancellationToken);
        return ingestion.Select(ApiKeyItems.From).Concat(agents.Select(ApiKeyItems.From)).OrderBy(x => x.Name).ToList();
    }
}

public class ManageApiKeyCommandHandler(DatabaseContext database, IIdProvider ids, ILogger<ManageApiKeyCommandHandler> logger, ApiKeyProtection protection)
    : IRequestHandler<ManageApiKeyCommand, ApiKeySecretResponse>
{
    public async ValueTask<ApiKeySecretResponse> Handle(ManageApiKeyCommand request, CancellationToken cancellationToken)
    {
        if (request.Kind is not ("ingestion" or "agent") || request.Action is not ("create" or "rotate" or "enable" or "delete" or "reveal")
            || request.Name == null || request.Name.Length > 64 || (request.Action == "create" && string.IsNullOrWhiteSpace(request.Name))
            || (request.Action != "create" && (!long.TryParse(request.Id, out var parsedId) || parsedId <= 0)))
            throw BusinessException.Throw(400, "凭据用途、操作、名称或编号无效");
        var create = request.Action == "create";
        var id = create ? ids.GetLongId() : long.Parse(request.Id!, System.Globalization.CultureInfo.InvariantCulture);
        var secret = request.Action is "create" or "rotate"
            ? (request.Kind == "ingestion" ? "sdi_" : "sda_") + Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant()
            : string.Empty;
        ApiKeyItem item;
        if (request.Kind == "ingestion")
        {
            var entity = create ? new IngestKeyEntity { Id = id, Name = request.Name.Trim() }
                : await database.IngestKeys.SingleOrDefaultAsync(x => x.Id == id, cancellationToken)
                    ?? throw BusinessException.Throw(404, "接入凭据不存在");
            if (request.Action == "reveal")
            {
                var revealed = protection.Reveal(request.Kind, id, entity.ProtectedSecret, entity.KeyHash);
                logger.LogInformation("凭据查看 {Kind} {KeyId} {UserId}", request.Kind, id, request.ContextUserId);
                return new(ApiKeyItems.From(entity), revealed);
            }
            if (secret.Length > 0)
            {
                entity.KeyHash = HashHelper.Sha256Hex(secret);
                entity.Prefix = secret[..12];
                entity.ProtectedSecret = protection.Protect(request.Kind, id, secret);
            }
            if (create) database.IngestKeys.Add(entity);
            if (request.Action == "enable") entity.Enabled = request.Enabled;
            if (request.Action == "delete") database.IngestKeys.Remove(entity);
            item = ApiKeyItems.From(entity);
            await database.SaveChangesAsync(cancellationToken);
            item = ApiKeyItems.From(entity);
        }
        else
        {
            var entity = create ? new AgentKeyEntity { Id = id, Name = request.Name.Trim() }
                : await database.AgentKeys.SingleOrDefaultAsync(x => x.Id == id, cancellationToken)
                    ?? throw BusinessException.Throw(404, "Agent 凭据不存在");
            if (request.Action == "reveal")
            {
                var revealed = protection.Reveal(request.Kind, id, entity.ProtectedSecret, entity.KeyHash);
                logger.LogInformation("凭据查看 {Kind} {KeyId} {UserId}", request.Kind, id, request.ContextUserId);
                return new(ApiKeyItems.From(entity), revealed);
            }
            if (secret.Length > 0)
            {
                entity.KeyHash = HashHelper.Sha256Hex(secret);
                entity.Prefix = secret[..12];
                entity.ProtectedSecret = protection.Protect(request.Kind, id, secret);
            }
            if (create) database.AgentKeys.Add(entity);
            if (request.Action == "enable") entity.Enabled = request.Enabled;
            if (request.Action == "delete") database.AgentKeys.Remove(entity);
            await database.SaveChangesAsync(cancellationToken);
            item = ApiKeyItems.From(entity);
        }

        ApiKeyEpoch.Invalidate();
        logger.LogInformation("凭据管理 {Kind} {KeyId} {Action} {UserId}", request.Kind, id, request.Action, request.ContextUserId);
        return new(item, secret);
    }
}

internal static class ApiKeyItems
{
    public static ApiKeyItem From(IngestKeyEntity entity) => new(entity.Id.ToString(), "ingestion", entity.Name, entity.Prefix,
        entity.Enabled, !string.IsNullOrEmpty(entity.ProtectedSecret), entity.CreateTime, entity.CreateUserId.ToString(), entity.UpdateTime, entity.UpdateUserId.ToString());
    public static ApiKeyItem From(AgentKeyEntity entity) => new(entity.Id.ToString(), "agent", entity.Name, entity.Prefix,
        entity.Enabled, !string.IsNullOrEmpty(entity.ProtectedSecret), entity.CreateTime, entity.CreateUserId.ToString(), entity.UpdateTime, entity.UpdateUserId.ToString());
}
