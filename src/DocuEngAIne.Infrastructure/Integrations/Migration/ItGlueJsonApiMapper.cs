using System.Text;
using System.Text.Json;
using DocuEngAIne.Core.Interfaces;

namespace DocuEngAIne.Infrastructure.Integrations.Migration;

/// <summary>
/// Maps IT Glue JSON:API (Compact <c>itg_list_organizations</c> or a fixture) into import DTOs.
/// Passwords and secret-shaped traits are dropped here so they never reach SQL.
/// </summary>
public static class ItGlueJsonApiMapper
{
    public const string OrganizationsToolName = "itg_list_organizations";
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 1000;
    public const string DocumentSlugPrefix = "itglue-doc-";
    public const string FlexibleAssetTypeName = "IT Glue Flexible Asset";
    public const string ItGlueIdFieldName = "IT Glue Id";

    public static string DocumentSlug(string itGlueId) => $"{DocumentSlugPrefix}{itGlueId}";

    public static ItGlueImportSlice Parse(string json)
    {
        var payload = UnwrapMcpPayload(json);
        var organizations = new List<ExternalCompanyDto>();
        var documents = new List<ItGlueDocumentDto>();
        var assets = new List<ItGlueFlexibleAssetDto>();
        var organizationRowCount = 0;
        var passwordsSkipped = 0;

        foreach (var resource in EnumerateResources(payload))
        {
            if (resource.ValueKind != JsonValueKind.Object)
                continue;

            var type = NormalizeType(ReadString(resource, "type"));
            if (IsPasswordType(type))
            {
                passwordsSkipped++;
                continue;
            }

            if (IsOrganizationType(type))
            {
                organizationRowCount++;
                var mapped = MapOrganization(resource);
                if (mapped is not null)
                    organizations.Add(mapped);
                continue;
            }

            if (IsDocumentType(type))
            {
                var mapped = MapDocument(resource);
                if (mapped is not null)
                    documents.Add(mapped);
                continue;
            }

            if (IsFlexibleAssetType(type))
            {
                var mapped = MapFlexibleAsset(resource);
                if (mapped is not null)
                    assets.Add(mapped);
            }
        }

        return new ItGlueImportSlice(organizations, documents, assets, organizationRowCount, passwordsSkipped);
    }

    public static string BuildOrganizationsArgumentsJson(int pageNumber, int pageSize = DefaultPageSize)
    {
        var size = Math.Clamp(pageSize, 1, MaxPageSize);
        return JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["pageNumber"] = pageNumber < 1 ? 1 : pageNumber,
            ["pageSize"] = size,
        });
    }

    public static async Task<ItGlueImportSlice> PullOrganizationsAsync(
        IMcpClient mcpClient,
        Guid mcpServerId,
        CancellationToken cancellationToken = default)
    {
        const int maxPages = 500;
        var organizations = new List<ExternalCompanyDto>();
        var skipped = 0;
        for (var page = 1; page <= maxPages; page++)
        {
            var args = BuildOrganizationsArgumentsJson(page);
            var body = await mcpClient.CallToolAsync(
                mcpServerId, OrganizationsToolName, args, cancellationToken);
            var pageSlice = Parse(body);
            organizations.AddRange(pageSlice.Organizations);
            skipped += pageSlice.PasswordsSkipped;
            if (pageSlice.OrganizationRowCount < DefaultPageSize)
                break;
        }

        return new ItGlueImportSlice(organizations, [], [], organizations.Count, skipped);
    }

    private static ExternalCompanyDto? MapOrganization(JsonElement resource)
    {
        var id = ReadString(resource, "id");
        var attributes = GetAttributes(resource);
        var name = ReadString(attributes, "name");
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name))
            return null;

        return new ExternalCompanyDto(
            ExternalId: id,
            Name: name.Trim(),
            Slug: ReadString(attributes, "short-name", "short_name", "shortName", "slug"),
            PrimaryDomain: ReadString(attributes, "primary-domain", "primary_domain", "primaryDomain", "domain"),
            City: ReadString(attributes, "city"),
            State: ReadString(attributes, "state"),
            Website: ReadString(attributes, "website", "web-site", "web_site"),
            Address: ReadString(attributes, "address", "address1", "address-1", "address_1"),
            IsInactive: ReadInactive(attributes));
    }

    private static ItGlueDocumentDto? MapDocument(JsonElement resource)
    {
        var id = ReadString(resource, "id");
        var attributes = GetAttributes(resource);
        var title = ReadString(attributes, "name", "title");
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(title))
            return null;

        return new ItGlueDocumentDto(
            ExternalId: id,
            Title: title.Trim(),
            Content: ReadString(attributes, "content", "body", "notes"),
            Summary: ReadString(attributes, "excerpt", "summary", "description"),
            OrganizationExternalId: ReadOrganizationId(resource, attributes));
    }

    private static ItGlueFlexibleAssetDto? MapFlexibleAsset(JsonElement resource)
    {
        var id = ReadString(resource, "id");
        var attributes = GetAttributes(resource);
        var name = ReadString(attributes, "name");
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name))
            return null;

        return new ItGlueFlexibleAssetDto(
            ExternalId: id,
            Name: name.Trim(),
            AssetTypeName: ReadString(attributes, "flexible-asset-type-name", "flexible_asset_type_name", "flexibleAssetTypeName")
                ?? FlexibleAssetTypeName,
            OrganizationExternalId: ReadOrganizationId(resource, attributes),
            Notes: SanitizeTraits(attributes));
    }

    /// <summary>Writes non-secret traits as readable notes. Password / token / totp keys are omitted.</summary>
    public static string? SanitizeTraits(JsonElement attributes)
    {
        if (attributes.ValueKind != JsonValueKind.Object)
            return null;
        if (!TryGetProperty(attributes, out var traits, "traits") || traits.ValueKind != JsonValueKind.Object)
            return null;

        var lines = new StringBuilder();
        foreach (var prop in traits.EnumerateObject())
        {
            if (IsSecretKey(prop.Name))
                continue;

            var value = prop.Value.ValueKind switch
            {
                JsonValueKind.String => prop.Value.GetString(),
                JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => prop.Value.GetRawText(),
                JsonValueKind.Null or JsonValueKind.Undefined => null,
                _ => null,
            };
            if (string.IsNullOrWhiteSpace(value))
                continue;

            if (lines.Length > 0)
                lines.AppendLine();
            lines.Append(prop.Name).Append(": ").Append(value);
        }

        return lines.Length == 0 ? null : lines.ToString();
    }

    public static bool IsSecretKey(string name)
    {
        var n = new string(name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        if (n.Length == 0)
            return false;

        return n.Contains("password", StringComparison.Ordinal)
            || n.Contains("passwd", StringComparison.Ordinal)
            || n.Contains("secret", StringComparison.Ordinal)
            || n.Contains("totp", StringComparison.Ordinal)
            || n.Contains("apikey", StringComparison.Ordinal)
            || n.Contains("privatekey", StringComparison.Ordinal)
            || n.Contains("passphrase", StringComparison.Ordinal)
            || n.Contains("credential", StringComparison.Ordinal)
            || n.EndsWith("token", StringComparison.Ordinal)
            || n.EndsWith("otp", StringComparison.Ordinal)
            || n is "pin";
    }

    private static string? ReadOrganizationId(JsonElement resource, JsonElement attributes)
    {
        var fromAttributes = ReadString(attributes, "organization-id", "organization_id", "organizationId");
        if (!string.IsNullOrWhiteSpace(fromAttributes))
            return fromAttributes;

        if (resource.ValueKind != JsonValueKind.Object
            || !TryGetProperty(resource, out var relationships, "relationships")
            || relationships.ValueKind != JsonValueKind.Object
            || !TryGetProperty(relationships, out var organization, "organization")
            || organization.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (TryGetProperty(organization, out var data, "data") && data.ValueKind == JsonValueKind.Object)
            return ReadString(data, "id");

        return ReadString(organization, "id");
    }

    private static bool? ReadInactive(JsonElement attributes)
    {
        if (TryGetProperty(attributes, out var archived, "archived", "is-archived", "is_archived")
            && archived.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            return archived.GetBoolean();
        }

        return null;
    }

    private static JsonElement GetAttributes(JsonElement resource)
    {
        if (resource.ValueKind == JsonValueKind.Object
            && TryGetProperty(resource, out var attributes, "attributes")
            && attributes.ValueKind == JsonValueKind.Object)
        {
            return attributes;
        }

        return resource;
    }

    private static IEnumerable<JsonElement> EnumerateResources(JsonElement payload)
    {
        if (payload.ValueKind == JsonValueKind.Array)
            return payload.EnumerateArray();

        if (payload.ValueKind == JsonValueKind.Object
            && TryGetProperty(payload, out var data, "data"))
        {
            if (data.ValueKind == JsonValueKind.Array)
                return data.EnumerateArray();
            if (data.ValueKind == JsonValueKind.Object)
                return [data];
        }

        return [];
    }

    private static string? NormalizeType(string? type)
        => string.IsNullOrWhiteSpace(type) ? null : type.Trim().ToLowerInvariant().Replace('-', '_');

    private static bool IsOrganizationType(string? type)
        => type is "organizations" or "organization";

    private static bool IsDocumentType(string? type)
        => type is "documents" or "document";

    private static bool IsFlexibleAssetType(string? type)
        => type is "flexible_assets" or "flexible_asset";

    private static bool IsPasswordType(string? type)
        => type is not null && (type.Contains("password", StringComparison.Ordinal) || type == "user_credentials");

    private static string? ReadString(JsonElement obj, params string[] names)
    {
        if (obj.ValueKind != JsonValueKind.Object)
            return null;
        if (!TryGetProperty(obj, out var value, names))
            return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => string.IsNullOrWhiteSpace(value.GetString()) ? null : value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True or JsonValueKind.False => value.GetRawText(),
            _ => null,
        };
    }

    private static bool TryGetProperty(JsonElement obj, out JsonElement value, params string[] names)
    {
        if (obj.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in obj.EnumerateObject())
            {
                foreach (var name in names)
                {
                    if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
                    {
                        value = prop.Value;
                        return true;
                    }
                }
            }
        }

        value = default;
        return false;
    }

    private static JsonElement UnwrapMcpPayload(string mcpBody)
    {
        JsonElement root;
        try
        {
            root = JsonSerializer.Deserialize<JsonElement>(mcpBody);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("IT Glue payload is not JSON.", ex);
        }

        if (root.ValueKind == JsonValueKind.Object && TryGetProperty(root, out var error, "error"))
        {
            var message = error.ValueKind == JsonValueKind.Object && TryGetProperty(error, out var msg, "message")
                ? msg.GetString()
                : error.GetRawText();
            throw new InvalidOperationException($"IT Glue MCP tool error: {message}");
        }

        var payload = root;
        if (root.ValueKind == JsonValueKind.Object && TryGetProperty(root, out var result, "result"))
            payload = result;

        if (payload.ValueKind == JsonValueKind.Object
            && TryGetProperty(payload, out var isError, "isError")
            && isError.ValueKind == JsonValueKind.True)
        {
            throw new InvalidOperationException(
                $"IT Glue MCP tool error: {ReadContentText(payload) ?? payload.GetRawText()}");
        }

        var text = ReadContentText(payload);
        if (!string.IsNullOrWhiteSpace(text))
        {
            var trimmed = text.TrimStart();
            if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
            {
                try
                {
                    return JsonSerializer.Deserialize<JsonElement>(text);
                }
                catch (JsonException)
                {
                    // fall through to the structured payload
                }
            }
        }

        return payload;
    }

    private static string? ReadContentText(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object)
            return null;
        if (!TryGetProperty(payload, out var content, "content") || content.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var item in content.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object
                && TryGetProperty(item, out var text, "text")
                && text.ValueKind == JsonValueKind.String)
            {
                return text.GetString();
            }
        }

        return null;
    }
}

public sealed record ItGlueImportSlice(
    IReadOnlyList<ExternalCompanyDto> Organizations,
    IReadOnlyList<ItGlueDocumentDto> Documents,
    IReadOnlyList<ItGlueFlexibleAssetDto> FlexibleAssets,
    int OrganizationRowCount,
    int PasswordsSkipped);

public sealed record ItGlueDocumentDto(
    string ExternalId,
    string Title,
    string? Content,
    string? Summary,
    string? OrganizationExternalId);

public sealed record ItGlueFlexibleAssetDto(
    string ExternalId,
    string Name,
    string AssetTypeName,
    string? OrganizationExternalId,
    string? Notes);
