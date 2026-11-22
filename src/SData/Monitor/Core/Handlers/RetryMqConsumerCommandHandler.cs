using Mediator;
using SData.Ingest;
using SData.Infra.Exceptions;

namespace SData.Monitor.Handlers;

public class RetryMqConsumerCommandHandler(IMqConsumerControl control) : IRequestHandler<RetryMqConsumerCommand, bool>
{
    public ValueTask<bool> Handle(RetryMqConsumerCommand request, CancellationToken cancellationToken)
    {
        if (!control.RequestRetry(request.InstanceId))
            throw new BusinessException("实例不存在、已停用或消费配置不完整") { StatusCode = 400 };
        return ValueTask.FromResult(true);
    }
}
