using SData.Rum;

namespace SData.Store.Tests;

/// <summary>存储分派测试只替换来源策略；真实项目校验由 RumOriginPolicyTests 覆盖.</summary>
internal sealed class RumTestOriginPolicy : IRumOriginPolicy
{
    public bool Allows(string projectKey, string? origin) =>
        (projectKey == "rum_demo" || projectKey == "other") && origin == "https://intranet.local";

    public bool AllowsAny(string? origin) => origin == "https://intranet.local";
}
