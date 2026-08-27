namespace DocuEngAIne.Core.Interfaces;

public interface ISecretEncryptionService
{
    string Encrypt(string plainText, string keyVersion);
    string Decrypt(string cipherText, string keyVersion);
}
