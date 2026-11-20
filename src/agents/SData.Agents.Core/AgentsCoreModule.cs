using Maomi;
using Microsoft.Extensions.DependencyInjection;

namespace SData.Agents;

[InjectModule<AgentsSharedModule>]
[InjectModule<AgentsApiModule>]
public class AgentsCoreModule : IModule
{
    public void ConfigureServices(ServiceContext context)
    {
        context.Services.AddScoped<IAgentQueries, AgentQueries>();
        context.Services.AddScoped<IAgentAnalysis, AgentAnalysis>();
        context.Services.AddSingleton<AgentRequestBudget>();
    }
}
