using System.Security.Cryptography;
using SData.Infra;

namespace SData.Infra.Configuration;

/// <summary>
/// RSA 密钥提供者实现：密钥持久化在 configs/rsa_private.key（XML 格式），首次启动自动生成.
/// </summary>
public class RsaProvider : IRsaProvider
{
    private readonly object _lock = new();
    private RSAParameters? _parameters;
    private readonly string _keyPath;

    /// <summary>
    /// Initializes a new instance of the <see cref="RsaProvider"/> class.
    /// </summary>
    public RsaProvider()
    {
        _keyPath = Path.Combine(AppConst.GetPath(AppConst.ConfigsDirectory), "rsa_private.key");
    }

    /// <inheritdoc/>
    public RSAParameters GetPrivateKey()
    {
        if (_parameters != null)
        {
            return _parameters.Value;
        }

        lock (_lock)
        {
            if (_parameters != null)
            {
                return _parameters.Value;
            }

            var directory = Path.GetDirectoryName(_keyPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            using var rsa = RSA.Create(2048);
            if (File.Exists(_keyPath))
            {
                rsa.FromXmlString(File.ReadAllText(_keyPath));
            }
            else
            {
                File.WriteAllText(_keyPath, rsa.ToXmlString(includePrivateParameters: true));
            }

            _parameters = rsa.ExportParameters(includePrivateParameters: true);
            return _parameters.Value;
        }
    }

    /// <inheritdoc/>
    public string GetPublicPem()
    {
        using var rsa = RSA.Create(GetPrivateKey());
        return rsa.ExportSubjectPublicKeyInfoPem();
    }
}
