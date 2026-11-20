using Maomi;
using SData.Analytics;
using SData.Alerts;
using SData.Auth;
using SData.Dashboard;
using SData.Database.Sqlite;
using SData.Ingest;
using SData.Infra;
using SData.Iot;
using SData.Logs;
using SData.Metrics;
using SData.Monitor;
using SData.Rum;
using SData.Settings;
using SData.Store;
using SData.Traces;

namespace SData;

/// <summary>
/// 主模块：聚合全部领域模块与宿主横切模块.
/// </summary>
[InjectModule<SData.Agents.AgentsCoreModule>]
[InjectModule<InfraCoreModule>]
[InjectModule<DatabaseSqliteModule>]
[InjectModule<StoreCoreModule>]
[InjectModule<AuthCoreModule>]
[InjectModule<IngestCoreModule>]
[InjectModule<LogsCoreModule>]
[InjectModule<TracesCoreModule>]
[InjectModule<MetricsCoreModule>]
[InjectModule<IotCoreModule>]
[InjectModule<RumCoreModule>]
[InjectModule<AnalyticsCoreModule>]
[InjectModule<AlertsCoreModule>]
[InjectModule<DashboardCoreModule>]
[InjectModule<SettingsCoreModule>]
[InjectModule<MonitorCoreModule>]
[InjectModule<SDataApiModule>]
[InjectModule<ConfigureGrpcModule>]
[InjectModule<ConfigureSignalRModule>]
public partial class MainModule : IModule
{
    /// <inheritdoc/>
    public void ConfigureServices(ServiceContext context)
    {
    }
}
