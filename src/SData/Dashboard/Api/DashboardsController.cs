using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SData.Infra;
using SData.Infra.Services;

namespace SData.Dashboard.Api;

/// <summary>
/// 仪表盘接口.
/// </summary>
[ApiController]
[Route("dashboards")]
[Authorize]
public class DashboardsController : ControllerBase
{
    private readonly IMediator _mediator;
    private readonly IUserContextProvider _userContextProvider;

    /// <summary>
    /// Initializes a new instance of the <see cref="DashboardsController"/> class.
    /// </summary>
    /// <param name="mediator">mediator.</param>
    /// <param name="userContextProvider">用户上下文.</param>
    public DashboardsController(IMediator mediator, IUserContextProvider userContextProvider)
    {
        _mediator = mediator;
        _userContextProvider = userContextProvider;
    }

    /// <summary>
    /// 仪表盘列表.
    /// </summary>
    /// <param name="command">分页.</param>
    /// <param name="cancellationToken">取消.</param>
    /// <returns>分页.</returns>
    [HttpGet]
    public async Task<Infra.Models.PagedResult<DashboardItem>> Query([FromQuery] QueryDashboardsCommand command, CancellationToken cancellationToken)
    {
        return await _mediator.Send(command, cancellationToken);
    }

    /// <summary>
    /// 保存仪表盘.
    /// </summary>
    /// <param name="command">请求.</param>
    /// <param name="cancellationToken">取消.</param>
    /// <returns>id.</returns>
    [HttpPost]
    [Authorize(Roles = SDataRoles.AdminOrWrite)]
    public async Task<Infra.Models.Simple<long>> Save([FromBody] SaveDashboardCommand command, CancellationToken cancellationToken)
    {
        _userContextProvider.SetUserContext(command);
        return await _mediator.Send(command, cancellationToken);
    }

    /// <summary>
    /// 仪表盘详情.
    /// </summary>
    /// <param name="id">id.</param>
    /// <param name="cancellationToken">取消.</param>
    /// <returns>详情.</returns>
    [HttpGet("{id}")]
    public async Task<DashboardItem> Get(long id, CancellationToken cancellationToken)
    {
        return await _mediator.Send(new GetDashboardCommand { Id = id }, cancellationToken);
    }

    /// <summary>
    /// 删除仪表盘.
    /// </summary>
    /// <param name="id">id.</param>
    /// <param name="cancellationToken">取消.</param>
    /// <returns>空.</returns>
    [HttpDelete("{id}")]
    [Authorize(Roles = SDataRoles.AdminOrWrite)]
    public async Task<Infra.Models.EmptyCommandResponse> Delete(long id, CancellationToken cancellationToken)
    {
        var command = new DeleteDashboardCommand { Id = id };
        _userContextProvider.SetUserContext(command);
        return await _mediator.Send(command, cancellationToken);
    }

    /// <summary>
    /// 拉取仪表盘面板数据.
    /// </summary>
    /// <param name="id">id.</param>
    /// <param name="from">起始时间覆盖（缺省 = 仪表盘时间配置）.</param>
    /// <param name="to">结束时间覆盖（缺省 = 当前时间）.</param>
    /// <param name="rowLimit">行数上限.</param>
    /// <param name="cancellationToken">取消.</param>
    /// <returns>面板数据.</returns>
    [HttpGet("{id}/data")]
    public async Task<GetDashboardDataCommandResponse> Data(long id, [FromQuery] DateTimeOffset? from, [FromQuery] DateTimeOffset? to, [FromQuery] int rowLimit = 1000, CancellationToken cancellationToken = default)
    {
        return await _mediator.Send(new GetDashboardDataCommand { Id = id, From = from, To = to, RowLimit = Math.Clamp(rowLimit, 1, 10_000) }, cancellationToken);
    }

    /// <summary>
    /// 面板查询预览（编辑器实时预览，无需先保存）.
    /// </summary>
    /// <param name="command">面板配置 + 时间范围.</param>
    /// <param name="cancellationToken">取消.</param>
    /// <returns>面板数据.</returns>
    [HttpPost("preview-panel")]
    public async Task<PanelData> PreviewPanel([FromBody] PreviewPanelCommand command, CancellationToken cancellationToken)
    {
        _userContextProvider.SetUserContext(command);
        return await _mediator.Send(command, cancellationToken);
    }
}
