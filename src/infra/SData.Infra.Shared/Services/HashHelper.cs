using System.Security.Cryptography;

namespace SData.Infra.Services;

/// <summary>
/// 常规哈希工具.
/// </summary>
public static class HashHelper
{
    /// <summary>
    /// SHA256 hex.
    /// </summary>
    /// <param name="value">原文.</param>
    /// <returns>hex 字符串.</returns>
    public static string Sha256Hex(string value)
    {
        var bytes = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
