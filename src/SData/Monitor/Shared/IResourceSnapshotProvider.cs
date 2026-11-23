namespace SData.Monitor;

/// <summary>
/// 最新资源快照提供器（ResourceSampler 实现；供自适应调参等跨域消费方读取，5 秒采样一次）.
/// </summary>
public interface IResourceSnapshotProvider
{
    /// <summary>最近一次采样快照（服务启动初期 State=unknown，字段可为空）.</summary>
    ResourceSnapshot Latest { get; }
}
