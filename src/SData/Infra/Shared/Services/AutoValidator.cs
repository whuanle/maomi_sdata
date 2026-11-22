using FluentValidation;

namespace SData.Infra.Services;

/// <summary>
/// 将 IModelValidator&lt;T&gt;.Validate 静态规则适配为 FluentValidation IValidator&lt;T&gt;.
/// </summary>
/// <typeparam name="T">被验证模型类型.</typeparam>
public class AutoValidator<T> : AbstractValidator<T>
    where T : class, IModelValidator<T>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="AutoValidator{T}"/> class.
    /// </summary>
    public AutoValidator()
    {
        T.Validate(this);
    }
}
