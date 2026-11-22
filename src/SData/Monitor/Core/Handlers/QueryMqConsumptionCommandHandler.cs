using Mediator;
using SData.Ingest;
using SData.Settings;
using SData.Store.Models;

namespace SData.Monitor.Handlers;

/// <summary>配置过滤与实时状态合并；历史采样不用于推断当前连接状态.</summary>
public class QueryMqConsumptionCommandHandler(ISystemSettingsProvider settings, IMqConsumptionStore store, SData.Store.IIngestBuffer? buffer = null)
    : IRequestHandler<QueryMqConsumptionCommand, QueryMqConsumptionResponse>
{
    public async ValueTask<QueryMqConsumptionResponse> Handle(QueryMqConsumptionCommand request, CancellationToken cancellationToken)
    {
        var runtime = MqRuntime.Snapshot().ToDictionary(x => x.InstanceId);
        var configured = settings.GetMqInstances().Where(x => !request.InstanceId.HasValue || x.Id == request.InstanceId).ToList();
        var instances = configured.Select(instance =>
        {
            runtime.TryGetValue(instance.Id, out var item);
            item ??= new MqRuntimeItem { InstanceId = instance.Id, State = "unknown" };
            if (!instance.Enabled) item.State = "disabled";
            item.Topics = instance.Topics.Select(rule =>
            {
                var topic = item.Topics.FirstOrDefault(x => x.Name == rule.Name && x.MatchMode == rule.MatchMode)
                    ?? new MqTopicRuntimeItem { Name = rule.Name, MatchMode = rule.MatchMode, State = item.State };
                if (!instance.Enabled) topic.State = "disabled";
                return topic;
            }).ToList();
            return item;
        }).ToList();
        IReadOnlyList<MqConsumptionRecord> history = [];
        if (request.From.HasValue && request.To.HasValue)
        {
            history = await store.QueryAsync(request.From.Value, request.To.Value, request.InstanceId, cancellationToken);
            var ids = configured.Select(x => x.Id).ToHashSet();
            history = history.Where(x => ids.Contains(x.InstanceId)).ToList();
        }

        return new QueryMqConsumptionResponse
        {
            Instances = instances, History = history, HistoryTruncated = history.Count >= 10000,
            WriterFailure = buffer?.GetStats().WriterFailure,
            Notifications = MqNotificationRuntime.Snapshot().Where(x => configured.Any(i => i.Id == x.InstanceId)).ToList(),
        };
    }
}
