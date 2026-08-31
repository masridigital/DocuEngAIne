using Microsoft.Extensions.Configuration;

namespace DocuEngAIne.Infrastructure.Llm;

/// <summary>
/// Resolves LLM API keys from configuration / Key Vault names. Values are never logged here.
/// </summary>
internal static class LlmSecretResolver
{
    public static string? Get(IConfiguration configuration, string secretName)
    {
        if (string.IsNullOrWhiteSpace(secretName))
            return null;

        return FirstNonEmpty(
            configuration[secretName],
            configuration[$"KeyVault:{secretName}"],
            Environment.GetEnvironmentVariable(secretName),
            Environment.GetEnvironmentVariable(secretName.Replace('-', '_').ToUpperInvariant()));
    }

    public static string Require(IConfiguration configuration, string secretName)
    {
        var value = Get(configuration, secretName);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(
                $"{secretName} is not configured. Set the {secretName} configuration value or Azure Key Vault secret.");
        }

        return value;
    }

    private static string? FirstNonEmpty(params string?[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate))
                return candidate;
        }

        return null;
    }
}
