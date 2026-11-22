using Mediator;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SData.Infra;
using SData.Infra.Configuration;
using SData.Infra.Services;

namespace SData.Settings.Api;

/// <summary>
/// 系统设置接口（整页管理员；响应含掩码后的 SecretKey，写入/测试立即生效项见各 Handler）.
/// </summary>
[ApiController]
[Route("settings")]
[Authorize(Roles = SDataRoles.Admin)]
public class SettingsController : ControllerBase
{
    private readonly IMediator _mediator;
    private readonly IUserContextProvider _userContextProvider;

    /// <summary>
    /// Initializes a new instance of the <see cref="SettingsController"/> class.
    /// </summary>
    /// <param name="mediator">mediator.</param>
    public SettingsController(IMediator mediator, IUserContextProvider userContextProvider)
    {
        _mediator = mediator;
        _userContextProvider = userContextProvider;
    }

    [HttpGet("api-keys")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public ValueTask<List<ApiKeyItem>> QueryApiKeys(CancellationToken cancellationToken)
        => _mediator.Send(new QueryApiKeysCommand(), cancellationToken);

    [HttpPost("api-keys")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public ValueTask<ApiKeySecretResponse> ManageApiKey([FromBody] ManageApiKeyCommand command, CancellationToken cancellationToken)
    {
        // 异常处理中间件可能清除已有响应头；发送前再次保证秘密响应不可缓存。
        Response.OnStarting(() =>
        {
            Response.Headers.CacheControl = "no-store, no-cache";
            Response.Headers.Pragma = "no-cache";
            return Task.CompletedTask;
        });
        _userContextProvider.SetUserContext(command);
        return _mediator.Send(command, cancellationToken);
    }

    [HttpGet("mqtt-auth")]
    public ValueTask<MqttAuthSettingsResponse> QueryMqttAuth(CancellationToken cancellationToken)
        => _mediator.Send(new QueryMqttAuthSettingsCommand(), cancellationToken);

    [HttpPost("mqtt-auth")]
    public ValueTask<MqttAuthSettingsResponse> SaveMqttAuth([FromBody] SaveMqttAuthSettingsCommand command, CancellationToken cancellationToken)
    {
        _userContextProvider.SetUserContext(command);
        return _mediator.Send(command, cancellationToken);
    }

    [HttpGet("notification-channels")]
    public ValueTask<List<NotificationChannelItem>> QueryNotifications(CancellationToken cancellationToken)
        => _mediator.Send(new QueryNotificationChannelsCommand(), cancellationToken);

    [HttpPut("notification-channels")]
    public ValueTask<NotificationChannelItem> SaveNotification([FromBody] SaveNotificationChannelCommand command, CancellationToken cancellationToken)
    {
        _userContextProvider.SetUserContext(command);
        return _mediator.Send(command, cancellationToken);
    }

    [HttpDelete("notification-channels/{id}")]
    public ValueTask<bool> DeleteNotification(string id, [FromQuery] long version, CancellationToken cancellationToken)
    {
        var command = new DeleteNotificationChannelCommand { Id = id, Version = version };
        _userContextProvider.SetUserContext(command);
        return _mediator.Send(command, cancellationToken);
    }

    [HttpPost("notification-channels/test")]
    public ValueTask<NotificationSendResult> TestNotification([FromBody] TestNotificationChannelCommand command, CancellationToken cancellationToken)
    {
        _userContextProvider.SetUserContext(command);
        return _mediator.Send<NotificationSendResult>(command, cancellationToken);
    }

    /// <summary>
    /// 系统设置（合并视图；SecretKey 掩码）.
    /// </summary>
    /// <param name="cancellationToken">取消.</param>
    /// <returns>设置.</returns>
    [HttpGet]
    public async Task<SettingsResponse> Query(CancellationToken cancellationToken)
    {
        return await _mediator.Send(new QuerySettingsCommand(), cancellationToken);
    }

    /// <summary>保存 DuckDB 内存限制（立即生效）.</summary>
    [HttpPut("duckdb")]
    public ValueTask<DuckDbSettingsResponse> SaveDuckDb([FromBody] SaveDuckDbSettingsCommand command, CancellationToken cancellationToken)
        => _mediator.Send(command, cancellationToken);

    /// <summary>
    /// 保存 S3 冷存储设置（立即重建 SECRET，立即生效）.
    /// </summary>
    /// <param name="command">请求.</param>
    /// <param name="cancellationToken">取消.</param>
    /// <returns>保存后的 S3 设置（掩码）.</returns>
    [HttpPut("s3")]
    public async Task<S3SettingsModel> SaveS3([FromBody] SaveS3SettingsCommand command, CancellationToken cancellationToken)
    {
        return await _mediator.Send(command, cancellationToken);
    }

    /// <summary>
    /// S3 连接测试（SecretKey 空/掩码 = 沿用已存值）.
    /// </summary>
    /// <param name="command">请求.</param>
    /// <param name="cancellationToken">取消.</param>
    /// <returns>测试结果.</returns>
    [HttpPost("s3/test")]
    public async Task<TestS3ConnectionResponse> TestS3([FromBody] TestS3ConnectionCommand command, CancellationToken cancellationToken)
    {
        return await _mediator.Send(command, cancellationToken);
    }

    /// <summary>
    /// 保存三段保留设置（下一轮保留/归档 tick 生效）.
    /// </summary>
    /// <param name="command">请求.</param>
    /// <param name="cancellationToken">取消.</param>
    /// <returns>保存后的保留计划.</returns>
    [HttpPut("retention")]
    public async Task<RetentionPlanOptions> SaveRetention([FromBody] SaveRetentionSettingsCommand command, CancellationToken cancellationToken)
    {
        return await _mediator.Send(command, cancellationToken);
    }

    /// <summary>
    /// 保存数据压缩设置（下一轮归档 tick 生效）.
    /// </summary>
    /// <param name="command">请求.</param>
    /// <param name="cancellationToken">取消.</param>
    /// <returns>保存后的压缩配置.</returns>
    /// <summary>
    /// 保存运行参数（全量；DuckDB 线程与并发即时生效，其余字段下一次读取生效）.
    /// </summary>
    /// <param name="command">请求.</param>
    /// <param name="cancellationToken">取消.</param>
    /// <returns>保存后的运行参数.</returns>
    [HttpPut("runtime")]
    public async Task<RuntimeSettings> SaveRuntime([FromBody] SaveRuntimeSettingsCommand command, CancellationToken cancellationToken)
    {
        return await _mediator.Send(command, cancellationToken);
    }

    [HttpPut("compression")]
    public async Task<CompressionOptions> SaveCompression([FromBody] SaveCompressionSettingsCommand command, CancellationToken cancellationToken)
    {
        return await _mediator.Send(command, cancellationToken);
    }

    /// <summary>
    /// MQ 日志消费实例列表（多实例，含每 topic 解析规则）.
    /// </summary>
    /// <param name="cancellationToken">取消.</param>
    /// <returns>实例列表.</returns>
    [HttpGet("mq-instances")]
    public async Task<List<MqInstanceOptions>> QueryMqInstances(CancellationToken cancellationToken)
    {
        return await _mediator.Send(new QueryMqInstancesCommand(), cancellationToken);
    }

    /// <summary>
    /// 保存 MQ 日志消费实例列表（全量替换；保存后由消费管理器周期检测自动热更新，无需重启）.
    /// </summary>
    /// <param name="command">请求.</param>
    /// <param name="cancellationToken">取消.</param>
    /// <returns>空.</returns>
    [HttpPut("mq-instances")]
    public async Task<SData.Infra.Models.EmptyCommandResponse> SaveMqInstances([FromBody] SaveMqInstancesCommand command, CancellationToken cancellationToken)
    {
        return await _mediator.Send(command, cancellationToken);
    }

    /// <summary>
    /// MQ 日志解析预览（样例文本按解析规则试解析，不落库）.
    /// </summary>
    /// <param name="command">请求.</param>
    /// <param name="cancellationToken">取消.</param>
    /// <returns>解析结果.</returns>
    [HttpPost("mq-instances/parse-preview")]
    public async Task<MqParsePreviewResult> ParsePreview([FromBody] ParsePreviewCommand command, CancellationToken cancellationToken)
    {
        return await _mediator.Send(command, cancellationToken);
    }

    /// <summary>
    /// 保存自身遥测导出设置（重启生效）.
    /// </summary>
    /// <param name="command">请求.</param>
    /// <param name="cancellationToken">取消.</param>
    /// <returns>保存后的 OTLP 配置.</returns>
    [HttpPut("otlp")]
    public async Task<OtlpOptions> SaveOtlp([FromBody] SaveOtlpSettingsCommand command, CancellationToken cancellationToken)
    {
        return await _mediator.Send(command, cancellationToken);
    }
}
