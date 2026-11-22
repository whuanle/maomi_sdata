using FluentValidation;
using Mediator;
using SData.Infra.Services;

namespace SData.Monitor;

/// <summary>请求管理器串行重建实例，不重放已确认消息.</summary>
public class RetryMqConsumerCommand : IRequest<bool>, IModelValidator<RetryMqConsumerCommand>
{
    public long InstanceId { get; set; }

    public static void Validate(AbstractValidator<RetryMqConsumerCommand> validator)
        => validator.RuleFor(x => x.InstanceId).GreaterThan(0);
}
