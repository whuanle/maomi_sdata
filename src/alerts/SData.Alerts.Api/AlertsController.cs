using Maomi;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SData.Infra;
using SData.Infra.Services;

namespace SData.Alerts.Api;

public class AlertsApiModule : IModule
{
    public void ConfigureServices(ServiceContext context) { }
}

[ApiController]
[Route("alerts")]
[Authorize]
public class AlertsController(IMediator mediator, IUserContextProvider user) : ControllerBase
{
    [HttpGet]
    public Task<List<AlertItem>> Query([FromQuery] QueryAlertsCommand command, CancellationToken token)
        => mediator.Send(command, token);

    [HttpGet("{id}/history")]
    public Task<AlertHistory> History(string id, CancellationToken token)
        => mediator.Send(new QueryAlertHistoryCommand { Id = id }, token);

    [HttpGet("channels")]
    [Authorize(Roles = SDataRoles.AdminOrWrite)]
    public Task<List<AlertChannelItem>> Channels(CancellationToken token)
        => mediator.Send(new QueryAlertChannelsCommand(), token);

    [HttpPost]
    [Authorize(Roles = SDataRoles.AdminOrWrite)]
    public Task<AlertItem> Save([FromBody] SaveAlertCommand command, CancellationToken token)
    {
        user.SetUserContext(command);
        return mediator.Send(command, token);
    }

    [HttpDelete("{id}")]
    [Authorize(Roles = SDataRoles.AdminOrWrite)]
    public Task<bool> Delete(string id, [FromQuery] long version, CancellationToken token)
    {
        var command = new DeleteAlertCommand { Id = id, Version = version };
        user.SetUserContext(command);
        return mediator.Send(command, token);
    }

    [HttpPost("preview")]
    [Authorize(Roles = SDataRoles.AdminOrWrite)]
    public Task<AlertPoint> Preview([FromBody] PreviewAlertCommand command, CancellationToken token)
        => mediator.Send(command, token);
}
