using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SData.Infra;
using SData.Infra.Services;

namespace SData.Iot.Api;

/// <summary>
/// 设备与遥测接口.
/// </summary>
[ApiController]
[Route("iot/devices")]
[Authorize]
public class DevicesController : ControllerBase
{
    private readonly IMediator _mediator;
    private readonly IUserContextProvider _userContextProvider;

    /// <summary>
    /// Initializes a new instance of the <see cref="DevicesController"/> class.
    /// </summary>
    /// <param name="mediator">mediator.</param>
    /// <param name="userContextProvider">用户上下文.</param>
    public DevicesController(IMediator mediator, IUserContextProvider userContextProvider)
    {
        _mediator = mediator;
        _userContextProvider = userContextProvider;
    }

    /// <summary>
    /// 设备列表.
    /// </summary>
    /// <param name="command">查询条件.</param>
    /// <param name="cancellationToken">取消.</param>
    /// <returns>分页设备.</returns>
    [HttpGet]
    public async Task<Infra.Models.PagedResult<DeviceItem>> Query([FromQuery] QueryDevicesCommand command, CancellationToken cancellationToken)
    {
        return await _mediator.Send(command, cancellationToken);
    }

    /// <summary>
    /// 保存设备.
    /// </summary>
    /// <param name="command">请求.</param>
    /// <param name="cancellationToken">取消.</param>
    /// <returns>设备 id.</returns>
    [HttpPost]
    [Authorize(Roles = SDataRoles.AdminOrWrite)]
    public async Task<Infra.Models.Simple<long>> Save([FromBody] SaveDeviceCommand command, CancellationToken cancellationToken)
    {
        _userContextProvider.SetUserContext(command);
        return await _mediator.Send(command, cancellationToken);
    }

    /// <summary>
    /// 删除设备.
    /// </summary>
    /// <param name="id">设备 id.</param>
    /// <param name="cancellationToken">取消.</param>
    /// <returns>空.</returns>
    [HttpDelete("{id}")]
    [Authorize(Roles = SDataRoles.AdminOrWrite)]
    public async Task<Infra.Models.EmptyCommandResponse> Delete(long id, CancellationToken cancellationToken)
    {
        var command = new DeleteDeviceCommand { Id = id };
        _userContextProvider.SetUserContext(command);
        return await _mediator.Send(command, cancellationToken);
    }

    /// <summary>
    /// 设备遥测区间查询.
    /// </summary>
    /// <param name="command">查询条件.</param>
    /// <param name="cancellationToken">取消.</param>
    /// <returns>序列.</returns>
    [HttpGet("telemetry")]
    public async Task<QueryTelemetryCommandResponse> Telemetry([FromQuery] QueryTelemetryCommand command, CancellationToken cancellationToken)
    {
        return await _mediator.Send(command, cancellationToken);
    }

    /// <summary>
    /// 设备最新值.
    /// </summary>
    /// <param name="deviceKey">设备标识.</param>
    /// <param name="cancellationToken">取消.</param>
    /// <returns>最新值列表.</returns>
    [HttpGet("{deviceKey}/latest")]
    public async Task<List<DeviceLatestValue>> Latest(string deviceKey, CancellationToken cancellationToken)
    {
        return await _mediator.Send(new QueryDeviceLatestCommand { DeviceKey = deviceKey }, cancellationToken);
    }
}
