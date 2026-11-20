using System.Diagnostics;
using FluentValidation;
using Maomi;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using SData.Infra;

namespace SData;

/// <summary>
/// MediatR 配置：扫描全部模块程序集注册 Handler，注册管线行为（验证 + 链路 + 耗时日志）.
/// </summary>
public class ConfigureMediatRModule : IModule
{
    /// <inheritdoc/>
    public void ConfigureServices(ServiceContext context)
    {
        var assemblies = context.Modules.Select(x => x.Assembly).Distinct().ToArray();
        context.Services.AddMediatR(configuration =>
        {
            configuration.RegisterServicesFromAssemblies(assemblies);
            configuration.RegisterGenericHandlers = true;
        });

        context.Services.AddTransient(typeof(IPipelineBehavior<,>), typeof(MediatRPipelineBehavior<,>));
    }
}

/// <summary>
/// MediatR 管线行为：IModelValidator 验证 → Activity 链路 → 耗时日志.
/// </summary>
/// <typeparam name="TRequest">请求类型.</typeparam>
/// <typeparam name="TResponse">响应类型.</typeparam>
public class MediatRPipelineBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    private readonly ILogger<MediatRPipelineBehavior<TRequest, TResponse>> _logger;
    private readonly IEnumerable<IValidator<TRequest>> _validators;

    /// <summary>
    /// Initializes a new instance of the <see cref="MediatRPipelineBehavior{TRequest, TResponse}"/> class.
    /// </summary>
    /// <param name="logger">日志.</param>
    /// <param name="validators">验证器.</param>
    public MediatRPipelineBehavior(ILogger<MediatRPipelineBehavior<TRequest, TResponse>> logger, IEnumerable<IValidator<TRequest>> validators)
    {
        _logger = logger;
        _validators = validators;
    }

    /// <inheritdoc/>
    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        // 模型验证
        foreach (var validator in _validators)
        {
            var validationResult = await validator.ValidateAsync(request, cancellationToken);
            if (!validationResult.IsValid)
            {
                var errors = string.Join("; ", validationResult.Errors.Select(x => $"{x.PropertyName}: {x.ErrorMessage}"));
                throw new Infra.Exceptions.BusinessException(errors)
                {
                    StatusCode = 400,
                };
            }
        }

        var requestName = typeof(TRequest).Name;
        using var activity = AppConst.ActivitySource.StartActivity("mediatr:" + requestName);
        var started = DateTimeOffset.UtcNow;
        try
        {
            var response = await next();
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
