namespace SData.Rum;

/// <summary>RUM 项目来源校验；公开项目键不作为身份凭证.</summary>
public interface IRumOriginPolicy
{
    /// <summary>来源是否允许向指定项目采集.</summary>
    bool Allows(string projectKey, string? origin);

    /// <summary>来源是否允许进行跨域预检或探针握手.</summary>
    bool AllowsAny(string? origin);
}
