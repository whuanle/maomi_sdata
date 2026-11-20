using Maomi;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace SData.Infra.Configuration;

/// <summary>
/// 基础配置模块.
/// </summary>
public class InfraConfigurationModule : IModule
{
    private readonly IConfiguration _configuration;

    /// <summary>
    /// Initializes a new instance of the <see cref="InfraConfigurationModule"/> class.
    /// </summary>
    /// <param name="configuration">配置.</param>
    public InfraConfigurationModule(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    /// <inheritdoc/>
    public void ConfigureServices(ServiceContext context)
    {
        var systemOptions = _configuration.GetSection("SData").Get<SystemOptions>()
            ?? throw new FormatException("The system configuration (SData section) cannot be loaded.");

        var nestedMq = _configuration.GetSection("SData:Mq:Instances");
        if (nestedMq.Exists())
        {
            systemOptions.Mq = nestedMq.Get<List<MqInstanceOptions>>() ?? [];
        }

        context.Services.AddSingleton(systemOptions);
        context.Services.AddSingleton<IRsaProvider>(new RsaProvider());
    }
}
