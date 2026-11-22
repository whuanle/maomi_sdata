using Mediator;
using Microsoft.EntityFrameworkCore;
using SData.Database;
using SData.Infra.Exceptions;
using SData.Infra.Models;
using SData.Infra.Services;
using SData.Iot.Services;
using SData.Store;
using SData.Store.Models;

namespace SData.Iot.Handlers;

/// <summary>
/// 保存设备处理.
/// </summary>
public class SaveDeviceCommandHandler : IRequestHandler<SaveDeviceCommand, Simple<long>>
{
    private readonly DatabaseContext _databaseContext;
    private readonly IIdProvider _idProvider;
    private readonly IMqttDeviceRegistry _registry;

    /// <summary>
    /// Initializes a new instance of the <see cref="SaveDeviceCommandHandler"/> class.
    /// </summary>
    public SaveDeviceCommandHandler(DatabaseContext databaseContext, IIdProvider idProvider, IMqttDeviceRegistry registry)
    {
        _databaseContext = databaseContext;
        _idProvider = idProvider;
        _registry = registry;
    }

    /// <inheritdoc/>
    public async ValueTask<Simple<long>> Handle(SaveDeviceCommand request, CancellationToken cancellationToken)
    {
        // 标签 JSON 合法性校验
        try
        {
            System.Text.Json.JsonDocument.Parse(string.IsNullOrEmpty(request.TagsJson) ? "{}" : request.TagsJson);
        }
        catch (System.Text.Json.JsonException)
        {
            throw BusinessException.Throw(400, "标签必须是合法的 JSON 对象");
        }

        if (request.Id == 0)
        {
            var exists = await _databaseContext.Devices.AnyAsync(x => x.DeviceKey == request.DeviceKey, cancellationToken);
            if (exists)
            {
                throw BusinessException.Throw(409, "设备标识已存在");
            }

            var device = new Database.Entities.DeviceEntity
            {
                Id = _idProvider.GetLongId(),
                DeviceKey = request.DeviceKey,
                Name = request.Name,
                Group = request.Group,
                TagsJson = string.IsNullOrEmpty(request.TagsJson) ? "{}" : request.TagsJson,
                Enabled = request.Enabled,
            };

            _databaseContext.Devices.Add(device);
            await _databaseContext.SaveChangesAsync(cancellationToken);
            _registry.Invalidate();
            return new Simple<long> { Value = device.Id };
        }

        var existing = await _databaseContext.Devices.FirstOrDefaultAsync(x => x.Id == request.Id, cancellationToken)
            ?? throw BusinessException.Throw(404, "设备不存在");

        existing.DeviceKey = request.DeviceKey;
        existing.Name = request.Name;
        existing.Group = request.Group;
        existing.TagsJson = string.IsNullOrEmpty(request.TagsJson) ? "{}" : request.TagsJson;
        existing.Enabled = request.Enabled;
        await _databaseContext.SaveChangesAsync(cancellationToken);
        _registry.Invalidate();
        return new Simple<long> { Value = existing.Id };
    }
}

/// <summary>
/// 删除设备处理.
/// </summary>
public class DeleteDeviceCommandHandler : IRequestHandler<DeleteDeviceCommand, EmptyCommandResponse>
{
    private readonly DatabaseContext _databaseContext;
    private readonly IMqttDeviceRegistry _registry;

    /// <summary>
    /// Initializes a new instance of the <see cref="DeleteDeviceCommandHandler"/> class.
    /// </summary>
    public DeleteDeviceCommandHandler(DatabaseContext databaseContext, IMqttDeviceRegistry registry)
    {
        _databaseContext = databaseContext;
        _registry = registry;
    }

    /// <inheritdoc/>
    public async ValueTask<EmptyCommandResponse> Handle(DeleteDeviceCommand request, CancellationToken cancellationToken)
    {
        var deleted = await _databaseContext.Devices.Where(x => x.Id == request.Id)
            .ExecuteDeleteAsync(cancellationToken);
        if (deleted == 0)
        {
            throw BusinessException.Throw(404, "设备不存在");
        }

        _registry.Invalidate();
        return new EmptyCommandResponse();
    }
}

/// <summary>
/// 设备列表处理.
/// </summary>
public class QueryDevicesCommandHandler : IRequestHandler<QueryDevicesCommand, PagedResult<DeviceItem>>
{
    private readonly DatabaseContext _databaseContext;

    /// <summary>
    /// Initializes a new instance of the <see cref="QueryDevicesCommandHandler"/> class.
    /// </summary>
    public QueryDevicesCommandHandler(DatabaseContext databaseContext)
    {
        _databaseContext = databaseContext;
    }

    /// <inheritdoc/>
    public async ValueTask<PagedResult<DeviceItem>> Handle(QueryDevicesCommand request, CancellationToken cancellationToken)
    {
        var page = Math.Max(1, request.Page);
        var pageSize = Math.Clamp(request.PageSize, 1, 200);
        var query = _databaseContext.Devices.AsNoTracking().OrderByDescending(x => x.Id);

        if (!string.IsNullOrEmpty(request.Search))
        {
            var keyword = $"%{request.Search}%";
            query = query.Where(x =>
                EF.Functions.Like(x.DeviceKey, keyword) ||
                EF.Functions.Like(x.Name, keyword) ||
                EF.Functions.Like(x.Group, keyword)).OrderByDescending(x => x.Id);
        }

        if (!string.IsNullOrEmpty(request.Group))
        {
            query = query.Where(x => x.Group == request.Group).OrderByDescending(x => x.Id);
        }

        var count = await query.CountAsync(cancellationToken);
        var items = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(x => new DeviceItem
            {
                Id = x.Id,
                DeviceKey = x.DeviceKey,
                Name = x.Name,
                Group = x.Group,
                TagsJson = x.TagsJson,
                Enabled = x.Enabled,
                CreateTime = x.CreateTime,
                UpdateTime = x.UpdateTime,
            })
            .ToListAsync(cancellationToken);

        return new PagedResult<DeviceItem>
        {
            Page = page,
            PageSize = pageSize,
            Count = count,
            Data = items,
        };
    }
}

/// <summary>
/// 遥测查询处理.
/// </summary>
public class QueryTelemetryCommandHandler : IRequestHandler<QueryTelemetryCommand, QueryTelemetryCommandResponse>
{
    private readonly ITelemetryStore _telemetryStore;

    /// <summary>
    /// Initializes a new instance of the <see cref="QueryTelemetryCommandHandler"/> class.
    /// </summary>
    public QueryTelemetryCommandHandler(ITelemetryStore telemetryStore)
    {
        _telemetryStore = telemetryStore;
    }

    /// <inheritdoc/>
    public async ValueTask<QueryTelemetryCommandResponse> Handle(QueryTelemetryCommand request, CancellationToken cancellationToken)
    {
        var response = await _telemetryStore.QueryAsync(new TelemetryQuery
        {
            DeviceKey = request.DeviceKey,
            Signals = request.Signals,
            Start = request.Start ?? default,
            End = request.End ?? default,
            Aggregation = ParseAggregation(request.Aggregation),
            StepSeconds = request.StepSeconds,
        }, cancellationToken);

        return new QueryTelemetryCommandResponse
        {
            Series = response.SeriesList.Select(series => new TelemetrySeriesItem
            {
                Signal = series.Name,
                Labels = series.LabelsJson,
                Points = series.Points.Select(point => new TelemetryPoint
                {
                    TimestampMs = point.TimestampMs,
                    Value = point.Value,
                }).ToList(),
            }).ToList(),
        };
    }

    private static MetricAggregation ParseAggregation(string aggregation)
    {
        return aggregation.ToLowerInvariant() switch
        {
            "sum" => MetricAggregation.Sum,
            "max" => MetricAggregation.Max,
            "min" => MetricAggregation.Min,
            "count" => MetricAggregation.Count,
            "last" => MetricAggregation.Last,
            "p95" => MetricAggregation.P95,
            _ => MetricAggregation.Avg,
        };
    }
}

/// <summary>
/// 遥测统一查询处理（DSL，MaxQueryDays 约束在执行器内）.
/// </summary>
public class SearchTelemetryCommandHandler : IRequestHandler<SearchTelemetryCommand, SData.Store.QueryDsl.SearchDslResponse>
{
    private readonly SData.Store.QueryDsl.IQueryDslExecutor _executor;

    /// <summary>
    /// Initializes a new instance of the <see cref="SearchTelemetryCommandHandler"/> class.
    /// </summary>
    /// <param name="executor">DSL 执行器.</param>
    public SearchTelemetryCommandHandler(SData.Store.QueryDsl.IQueryDslExecutor executor)
    {
        _executor = executor;
    }

    /// <inheritdoc/>
    public async ValueTask<SData.Store.QueryDsl.SearchDslResponse> Handle(SearchTelemetryCommand request, CancellationToken cancellationToken)
    {
        return await _executor.ExecuteAsync(request, SData.Store.QueryDsl.DataDomain.Iot, cancellationToken);
    }
}

/// <summary>
/// 设备最新值处理.
/// </summary>
public class QueryDeviceLatestCommandHandler : IRequestHandler<QueryDeviceLatestCommand, List<DeviceLatestValue>>
{
    private readonly ITelemetryStore _telemetryStore;

    /// <summary>
    /// Initializes a new instance of the <see cref="QueryDeviceLatestCommandHandler"/> class.
    /// </summary>
    public QueryDeviceLatestCommandHandler(ITelemetryStore telemetryStore)
    {
        _telemetryStore = telemetryStore;
    }

    /// <inheritdoc/>
    public async ValueTask<List<DeviceLatestValue>> Handle(QueryDeviceLatestCommand request, CancellationToken cancellationToken)
    {
        var values = await _telemetryStore.LatestAsync(request.DeviceKey, cancellationToken);
        return values.Select(value => new DeviceLatestValue
        {
            Signal = value.Signal,
            NumValue = value.NumValue,
            StrValue = value.StrValue,
            TimestampMs = value.TimestampMs,
        }).ToList();
    }
}
