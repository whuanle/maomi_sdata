using Maomi;
using Microsoft.Extensions.DependencyInjection;

namespace SData.Agents;

/// <summary>只读 MCP HTTP 协议适配.</summary>
public class AgentsApiModule : IModule
{
    public void ConfigureServices(ServiceContext context)
    {
        context.Services.AddMcpServer().WithHttpTransport(options => options.Stateless = true).WithTools<AgentTools>();
    }
}
