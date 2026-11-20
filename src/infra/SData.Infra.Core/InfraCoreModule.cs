using FluentValidation;
using Maomi;
using MediatR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SData.Infra.Configuration;
using SData.Infra.Services;

namespace SData.Infra;

/// <summary>
/// 基础设施模块：注册通用服务与模型验证器自动发现.
/// </summary>
[InjectModule<InfraConfigurationModule>]
public class InfraCoreModule : ModuleCore
{
    private readonly IConfiguration _configuration;

    /// <summary>
    /// Initializes a new instance of the <see cref="InfraCoreModule"/> class.
    /// </summary>
    /// <param name="configuration">配置.</param>
    public InfraCoreModule(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    /// <inheritdoc/>
    public override void ConfigureServices(ServiceContext context)
    {
        context.Services.AddSingleton<IIdProvider>(new DefaultIdProvider((ushort)1));
        context.Services.AddHttpContextAccessor();
        context.Services.AddScoped<IUserContext, UserContext>();
        context.Services.AddScoped<IUserContextProvider, DefaultUserContextProvider>();
        context.Services.AddSingleton<IPasswordHasher, Pbkdf2PasswordHasher>();
    }

    /// <inheritdoc/>
    public override void TypeFilter(ServiceContext context, Type type)
    {
        if (type.IsClass)
        {
            var validator = type.GetInterfaces()
                .Where(x => x.IsGenericType && x.GetGenericTypeDefinition() == typeof(IModelValidator<>) && x.GenericTypeArguments[0] == type)
                .FirstOrDefault();
            if (validator == null)
            {
                return;
            }

            // 避免继承
            if (validator.GenericTypeArguments[0] != type)
            {
                return;
            }

            context.Services.AddScoped(typeof(IValidator<>).MakeGenericType(type), typeof(AutoValidator<>).MakeGenericType(type));
            context.Services.AddScoped(type);
        }
    }
}
