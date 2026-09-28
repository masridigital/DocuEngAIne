namespace DocuEngAIne.Infrastructure.Configuration;

/// <summary>
/// Placeholders for Azure AI Search. The admin/query key is never stored here — only the
/// Key Vault secret <em>name</em>. Empty values mean the in-memory stub is the active backend.
/// </summary>
public sealed class AzureSearchOptions
{
    public const string SectionName = "Azure:Search";

    public string IndexName { get; set; } = string.Empty;

    public string Endpoint { get; set; } = string.Empty;

    /// <summary>Key Vault secret name that holds the Azure AI Search key. Never the key itself.</summary>
    public string ApiKeySecretName { get; set; } = string.Empty;
}
