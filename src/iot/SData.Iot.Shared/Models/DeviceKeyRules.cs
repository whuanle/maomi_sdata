using System.Text.RegularExpressions;

namespace SData.Iot;

/// <summary>
/// 手工管理与 MQTT 接入共用的设备标识约束.
/// </summary>
public static class DeviceKeyRules
{
    /// <summary>设备标识格式（1–64 个 ASCII 字母、数字或 _-:.）.</summary>
    public const string Pattern = "^[a-zA-Z0-9_\\-:.]{1,64}\\z";

    /// <summary>判断设备标识是否合法.</summary>
    /// <param name="deviceKey">设备标识.</param>
    /// <returns>是否合法.</returns>
    public static bool IsValid(string deviceKey) =>
        !string.IsNullOrEmpty(deviceKey) && deviceKey.Length <= 64
        && Regex.IsMatch(deviceKey, Pattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
}
