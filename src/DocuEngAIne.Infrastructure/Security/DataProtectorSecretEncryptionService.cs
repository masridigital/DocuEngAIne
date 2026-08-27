using DocuEngAIne.Core.Interfaces;
using Microsoft.AspNetCore.DataProtection;
using System.Text;

namespace DocuEngAIne.Infrastructure.Security;

public class DataProtectorSecretEncryptionService : ISecretEncryptionService
{
    private readonly IDataProtectionProvider _provider;

    public DataProtectorSecretEncryptionService(IDataProtectionProvider provider)
    {
        _provider = provider;
    }

    public string Encrypt(string plainText, string keyVersion)
    {
        var protector = _provider.CreateProtector($"DocuEngAIne.Secret.{keyVersion}");
        return protector.Protect(plainText);
    }

    public string Decrypt(string cipherText, string keyVersion)
    {
        var protector = _provider.CreateProtector($"DocuEngAIne.Secret.{keyVersion}");
        return protector.Unprotect(cipherText);
    }
}
