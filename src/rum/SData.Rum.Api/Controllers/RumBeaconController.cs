using Microsoft.AspNetCore.Mvc;

namespace SData.Rum.Api.Controllers;

/// <summary>
/// RUM 上报端点（C3 协议）：beacon 摄取、采样配置、探针脚本.
/// </summary>
[ApiController]
[Microsoft.AspNetCore.Cors.EnableCors("RumLiveCors")]
public class RumBeaconController : ControllerBase
{
    private readonly IRumBeaconService _beaconService;

    /// <summary>
    /// Initializes a new instance of the <see cref="RumBeaconController"/> class.
    /// </summary>
    /// <param name="beaconService">beacon 摄取服务.</param>
    public RumBeaconController(IRumBeaconService beaconService)
    {
        _beaconService = beaconService;
    }

    /// <summary>
    /// RUM beacon 上报（gzip JSON；成功 204，键无效 401，缓冲满 429）.
    /// </summary>
    /// <returns>空响应.</returns>
    [HttpPost("/rum/v1/beacon")]
    public async Task<IActionResult> Beacon()
    {
        using var reader = new MemoryStream();
        await Request.Body.CopyToAsync(reader);
        var status = await _beaconService.IngestAsync(reader.ToArray(), Request.Headers["Content-Encoding"].FirstOrDefault(), RumRequestOrigin.Read(Request));
        return status switch
        {
            RumIngestStatus.Ok => NoContent(),
            RumIngestStatus.InvalidKey => Unauthorized(),
            RumIngestStatus.BufferFull => StatusCode(429),
            RumIngestStatus.Forbidden => StatusCode(403),
            _ => BadRequest(),
        };
    }

    /// <summary>
    /// RUM 采样配置（项目与来源校验通过后返回保存值）.
    /// </summary>
    /// <param name="projectKey">项目标识.</param>
    /// <returns>采样配置.</returns>
    [HttpGet("/rum/v1/config")]
    public IActionResult Config([FromQuery] string projectKey)
    {
        var config = _beaconService.GetConfig(projectKey, RumRequestOrigin.Read(Request));
        return config == null ? StatusCode(403) : Ok(config);
    }

    /// <summary>
    /// RUM 探针脚本（占位，T612 探针 SDK 替换）.
    /// </summary>
    /// <returns>JS 脚本.</returns>
    [HttpGet("/rum/probe.js")]
    public IActionResult Probe()
    {
        var path = Path.Combine(SData.Infra.AppConst.GetPath("wwwroot"), "rum", "probe.js");
        return System.IO.File.Exists(path)
            ? PhysicalFile(path, "application/javascript")
            : StatusCode(503, "探针尚未构建，请运行 ui 的 build:probe");
    }
}
