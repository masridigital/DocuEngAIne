using Azure.Extensions.AspNetCore.Configuration.Secrets;
using Azure.Identity;
using Microsoft.Extensions.Configuration;

namespace DocuEngAIne.Infrastructure.Configuration;

public static class KeyVaultExtensions
{
    public static IConfigurationBuilder AddAzureKeyVaultIfConfigured(this IConfigurationBuilder builder)
    {
        var tempConfig = builder.Build();
        var vaultUri = tempConfig["Azure:KeyVault:VaultUri"];

        if (string.IsNullOrWhiteSpace(vaultUri))
            return builder;

        builder.AddAzureKeyVault(
            new Uri(vaultUri),
            new ChainedTokenCredential(
                new ManagedIdentityCredential(ManagedIdentityId.SystemAssigned),
                new AzureCliCredential()));

        return builder;
    }
}
