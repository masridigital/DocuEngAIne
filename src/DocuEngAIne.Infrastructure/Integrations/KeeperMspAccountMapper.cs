using System.Text.Json;
using DocuEngAIne.Core.Interfaces;

namespace DocuEngAIne.Infrastructure.Integrations;

/// <summary>
/// Maps StackJack Compact <c>keeper_msp_list_accounts</c> JSON (vendor passthrough, often
/// JSON-RPC wrapped) to KeeperLink DTOs. Compact Keeper has no vault record list and no
/// password tools — this mapper never stores secrets and always leaves
/// <see cref="ExternalKeeperLinkDto.KeeperRecordUrl"/> null.
/// Each account uses <c>vendorInternalId</c> (MSP's own id, preferred) or <c>partnerId</c>
/// (Keeper's internal id) plus <c>name</c> → <see cref="ExternalKeeperLinkDto.Name"/>.
/// Skip rows missing both ids or missing <c>name</c>. No pagination — one shot, no arguments.
/// A JSON array is the list; an object with <c>accounts</c> is also accepted.
/// Do not call provision, usage, or lifecycle tools.
/// </summary>
public static class KeeperMspAccountMapper
{
    public const string ToolName = "keeper_msp_list_accounts";

    public static IReadOnlyList<ExternalKeeperLinkDto> MapAccounts(string mcpBody)
    {
        var payload = UnwrapMcpPayload(mcpBody);
        var links = new List<ExternalKeeperLinkDto>();
        foreach (var account in EnumerateAccounts(payload))
        {
            var mapped = MapAccount(account);
            if (mapped is not null)
                links.Add(mapped);
        }
        return links;
    }

    public static async Task<IReadOnlyList<ExternalKeeperLinkDto>> PullAsync(
        IMcpClient mcpClient,
        Guid mcpServerId,
        CancellationToken cancellationToken = default)
    {
        var body = await mcpClient.CallToolAsync(mcpServerId, ToolName, null, cancellationToken);
        return MapAccounts(body);
    }

    private static ExternalKeeperLinkDto? MapAccount(JsonElement account)
    {
        if (account.ValueKind != JsonValueKind.Object)
            return null;

        var id = ReadString(account, "vendorInternalId")
            ?? ReadString(account, "partnerId");
        var name = ReadString(account, "name");
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name))
            return null;

        return new ExternalKeeperLinkDto(
            ExternalId: id,
            Name: name.Trim(),
            UsernameHint: null,
            KeeperRecordUrl: null);
    }

    private static string? ReadString(JsonElement obj, params string[] names)
    {
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

    private static IEnumerable<JsonElement> EnumerateAccounts(JsonElement payload)
    {
        if (payload.ValueKind == JsonValueKind.Array)
            return payload.EnumerateArray();

        if (payload.ValueKind == JsonValueKind.Object
            && TryGetProperty(payload, out var accounts, "accounts")
            && accounts.ValueKind == JsonValueKind.Array)
        {
            return accounts.EnumerateArray();
        }

        return [];
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
            throw new InvalidOperationException("Keeper MCP tool returned non-JSON.", ex);
        }

        if (root.ValueKind == JsonValueKind.Object && TryGetProperty(root, out var error, "error"))
        {
            var message = error.ValueKind == JsonValueKind.Object && TryGetProperty(error, out var msg, "message")
                ? msg.GetString()
                : error.GetRawText();
            throw new InvalidOperationException($"Keeper MCP tool error: {message}");
        }

        var payload = root;
        if (root.ValueKind == JsonValueKind.Object && TryGetProperty(root, out var result, "result"))
            payload = result;

        if (payload.ValueKind == JsonValueKind.Object
            && TryGetProperty(payload, out var isError, "isError")
            && isError.ValueKind == JsonValueKind.True)
        {
            var errText = ReadContentText(payload);
            throw new InvalidOperationException($"Keeper MCP tool error: {errText ?? payload.GetRawText()}");
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
                    // fall through to structured payload
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
            if (item.ValueKind == JsonValueKind.Object && TryGetProperty(item, out var text, "text")
                && text.ValueKind == JsonValueKind.String)
            {
                return text.GetString();
            }
        }

        return null;
    }
}
