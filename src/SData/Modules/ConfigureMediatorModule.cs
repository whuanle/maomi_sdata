using System.Diagnostics;
using FluentValidation;
using Maomi;
using Mediator;
using Microsoft.Extensions.DependencyInjection;
using SData.Infra;

[assembly: MediatorOptions(ServiceLifetime = ServiceLifetime.Scoped)]

namespace SData;

/// <summary>
/// Mediator 配置：源生成器自动注册全部 Handler，开放泛型注册管线行为（验证 + 链路 + 耗时日志）.
/// </summary>
public class ConfigureMediatorModule : IModule
{
    /// <inheritdoc/>
    public void ConfigureServices(ServiceContext context)
    {
        // ServiceLifetime.Scoped 由文件头 MediatorOptions 声明：Handler 注入 Scoped 的 DatabaseContext，不能使用默认 Singleton。
        context.Services.AddMediator();
        context.Services.AddScoped(typeof(IPipelineBehavior<,>), typeof(MediatorPipelineBehavior<,>));
    }
}

/// <summary>
/// Mediator 管线行为：IModelValidator 验证 → Activity 链路 → 耗时日志.
/// </summary>
/// <typeparam name="TMessage">消息（请求）类型.</typeparam>
/// <typeparam name="TResponse">响应类型.</typeparam>
public class MediatorPipelineBehavior<TMessage, TResponse> : IPipelineBehavior<TMessage, TResponse>
    where TMessage : notnull, IMessage
{
    private readonly ILogger<MediatorPipelineBehavior<TMessage, TResponse>> _logger;
    private readonly IEnumerable<IValidator<TMessage>> _validators;

    /// <summary>
    /// Initializes a new instance of the <see cref="MediatorPipelineBehavior{TMessage, TResponse}"/> class.
    /// </summary>
    /// <param name="logger">日志.</param>
    /// <param name="validators">验证器.</param>
    public MediatorPipelineBehavior(ILogger<MediatorPipelineBehavior<TMessage, TResponse>> logger, IEnumerable<IValidator<TMessage>> validators)
    {
        _logger = logger;
        _validators = validators;
    }

    /// <inheritdoc/>
    public async ValueTask<TResponse> Handle(TMessage message, MessageHandlerDelegate<TMessage, TResponse> next, CancellationToken cancellationToken)
    {
        // 模型验证
        foreach (var validator in _validators)
        {
            var validationResult = await validator.ValidateAsync(message, cancellationToken);
            if (!validationResult.IsValid)
            {
                var errors = string.Join("; ", validationResult.Errors.Select(x => $"{x.PropertyName}: {x.ErrorMessage}"));
                throw new Infra.Exceptions.BusinessException(errors)
                {
                    StatusCode = 400,
                };
            }
        }

        var requestName = typeof(TMessage).Name;
        using var activity = AppConst.ActivitySource.StartActivity("mediator:" + requestName);
        var started = DateTimeOffset.UtcNow;
        try
        {
            var response = await next(message, cancellationToken);
            var elapsed = DateTimeOffset.UtcNow - started;
            _logger.LogInformation("{Request} completed in {ElapsedMs}ms", requestName, elapsed.TotalMilliseconds.ToString("F1"));
            return response;
        }
        catch (Exception exception)
        {
            activity?.SetTag("error", true);
            activity?.AddEvent(new ActivityEvent("exception", tags: new ActivityTagsCollection
            {
                ["exception.type"] = exception.GetType().Name,
                ["exception.message"] = exception.Message,
            }));
            throw;
        }
    }
}
