using Maomi;
using Mediator;
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
    public ValueTask<List<AlertItem>> Query([FromQuery] QueryAlertsCommand command, CancellationToken token)
        => mediator.Send(command, token);

    [HttpGet("{id}/history")]
    public ValueTask<AlertHistory> History(string id, CancellationToken token)
        => mediator.Send(new QueryAlertHistoryCommand { Id = id }, token);

    [HttpGet("channels")]
    [Authorize(Roles = SDataRoles.AdminOrWrite)]
    public ValueTask<List<AlertChannelItem>> Channels(CancellationToken token)
        => mediator.Send(new QueryAlertChannelsCommand(), token);

    [HttpPost]
    [Authorize(Roles = SDataRoles.AdminOrWrite)]
    public ValueTask<AlertItem> Save([FromBody] SaveAlertCommand command, CancellationToken token)
    {
        user.SetUserContext(command);
        return mediator.Send(command, token);
    }

    [HttpDelete("{id}")]
    [Authorize(Roles = SDataRoles.AdminOrWrite)]
    public ValueTask<bool> Delete(string id, [FromQuery] long version, CancellationToken token)
    {
        var command = new DeleteAlertCommand { Id = id, Version = version };
        user.SetUserContext(command);
        return mediator.Send(command, token);
    }

    [HttpPost("preview")]
    [Authorize(Roles = SDataRoles.AdminOrWrite)]
    public ValueTask<AlertPoint> Preview([FromBody] PreviewAlertCommand command, CancellationToken token)
        => mediator.Send(command, token);
}
