namespace SData.Infra.Services;

/// <summary>
/// 密码哈希（PBKDF2）.
/// </summary>
public interface IPasswordHasher
{
    /// <summary>
    /// 生成密码哈希（含盐，格式 pbkdf2$iterations$salt$hash）.
    /// </summary>
    /// <param name="password">明文密码.</param>
    /// <returns>哈希串.</returns>
    string Hash(string password);

    /// <summary>
    /// 校验密码.
    /// </summary>
    /// <param name="password">明文密码.</param>
    /// <param name="hash">存储的哈希串.</param>
    /// <returns>是否匹配.</returns>
    bool Verify(string password, string hash);
}
