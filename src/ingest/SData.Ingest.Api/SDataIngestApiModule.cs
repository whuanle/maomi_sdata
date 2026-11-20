using Maomi;
using Microsoft.Extensions.DependencyInjection;

namespace SData.Ingest.Api;

/// <summary>
/// SDataIngestApiModule.
/// </summary>
public class SDataIngestApiModule : IModule
{
    /// <inheritdoc/>
    public void ConfigureServices(ServiceContext context)
    {
        context.Services.AddScoped<IngestApiKeyFilter>();
    }
}
