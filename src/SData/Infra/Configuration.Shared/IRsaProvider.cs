using System.Security.Cryptography;

namespace SData.Infra.Configuration;

/// <summary>
/// RSA 密钥提供者（JWT 签名用），密钥文件缺失时自动生成.
/// </summary>
public interface IRsaProvider
{
    /// <summary>
    /// 获取 RSA 私钥参数.
    /// </summary>
    /// <returns>RSA 参数.</returns>
    RSAParameters GetPrivateKey();

    /// <summary>
    /// 获取公钥 PEM.
    /// </summary>
    /// <returns>PEM 字符串.</returns>
    string GetPublicPem();
}
