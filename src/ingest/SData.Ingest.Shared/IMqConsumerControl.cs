namespace SData.Ingest;

/// <summary>实例级消费控制；请求只排队，由管理器串行重建.</summary>
public interface IMqConsumerControl
{
    bool RequestRetry(long instanceId);
}
