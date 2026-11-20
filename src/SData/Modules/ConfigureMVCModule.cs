using Maomi;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using SData.Infra.Api.Conventions;
using System.Reflection;

namespace SData;

/// <summary>
/// MVC 配置：注册全部模块 Api 程序集、/api 路由前缀、JSON 序列化.
/// </summary>
public class ConfigureMVCModule : IModule
{
    /// <inheritdoc/>
    public void ConfigureServices(ServiceContext context)
    {
        var mvc = context.Services
            .AddControllers(options =>
            {
                options.Conventions.Add(new ApiRouteConvention("api"));
                options.SuppressImplicitRequiredAttributeForNonNullableReferenceTypes = true;
            })
            .AddJsonOptions(options =>
            {
                options.JsonSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
                options.JsonSerializerOptions.DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull;
            });

        // 注册所有模块程序集为 ApplicationPart（控制器发现），去重
        var added = new HashSet<Assembly>();
        foreach (var module in context.Modules)
        {
            if (added.Add(module.Assembly))
            {
                mvc.AddApplicationPart(module.Assembly);
            }
        }
    }
}
