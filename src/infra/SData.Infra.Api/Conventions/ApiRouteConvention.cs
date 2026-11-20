using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationModels;

namespace SData.Infra.Api.Conventions;

/// <summary>
/// 为所有控制器路由添加 /api 前缀.
/// </summary>
public class ApiRouteConvention : IApplicationModelConvention
{
    private readonly string _prefix;

    /// <summary>
    /// Initializes a new instance of the <see cref="ApiRouteConvention"/> class.
    /// </summary>
    /// <param name="prefix">路由前缀.</param>
    public ApiRouteConvention(string prefix)
    {
        _prefix = prefix;
    }

    /// <inheritdoc/>
    public void Apply(ApplicationModel application)
    {
        foreach (var controller in application.Controllers)
        {
            var selector = controller.Selectors.FirstOrDefault(x => x.AttributeRouteModel != null);
            if (selector == null)
            {
                continue;
            }

            selector.AttributeRouteModel = AttributeRouteModel.CombineAttributeRouteModel(
                new AttributeRouteModel(new RouteAttribute(_prefix)),
                selector.AttributeRouteModel);
        }
    }
}
