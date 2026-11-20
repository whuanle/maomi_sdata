using FluentValidation;

namespace MediatR;

/// <summary>
/// 模型验证接口：请求模型实现该接口，在 Validate 中声明 FluentValidation 规则.
/// 由 InfraCoreModule 自动注册为 IValidator&lt;T&gt;，MVC 自动验证管线调用.
/// </summary>
/// <typeparam name="T">模型类型.</typeparam>
public interface IModelValidator<T>
    where T : class, IModelValidator<T>
{
    /// <summary>
    /// 模型验证规则.
    /// </summary>
    /// <param name="validate">验证器.</param>
    static abstract void Validate(AbstractValidator<T> validate);
}
