using System.Globalization;
using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using SData.Infra.Exceptions;
using SData.Infra.Services;

namespace SData.Settings.Services;

/// <summary>查看材料与用途和记录绑定，鉴权仍只依赖哈希.</summary>
public class ApiKeyProtection(IDataProtectionProvider provider)
{
    private IDataProtector Protector(string kind, long id)
        => provider.CreateProtector("SData.ApiKeys.v1", kind, id.ToString(CultureInfo.InvariantCulture));

    public string Protect(string kind, long id, string secret) => Protector(kind, id).Protect(secret);

    public string Reveal(string kind, long id, string protectedSecret, string expectedHash)
    {
        if (string.IsNullOrEmpty(protectedSecret))
            throw BusinessException.Throw(409, "此 Key 仅保存历史哈希，请先重置后再查看；重置会使旧 Key 失效");
        try
        {
            var secret = Protector(kind, id).Unprotect(protectedSecret);
            if (!CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.ASCII.GetBytes(HashHelper.Sha256Hex(secret)),
                System.Text.Encoding.ASCII.GetBytes(expectedHash)))
                throw new CryptographicException();
            return secret;
        }
        catch (CryptographicException)
        {
            throw BusinessException.Throw(409, "Key 恢复材料不可用，请检查密钥环备份或重置 Key");
        }
    }
}
