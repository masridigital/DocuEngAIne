using System.Text.Json;
using System.Text.Json.Serialization;
using DocuEngAIne.Api.Endpoints;
using DocuEngAIne.Core.Interfaces;
using DocuEngAIne.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace DocuEngAIne.Api.Mcp;

/// <summary>
/// Read-only outbound MCP catalog. Every tool query is <c>ForTenant</c> on the token-mapped
/// <see cref="ICurrentUser"/>. Keeper reveal is not a tool — list_keeper_links returns titles and
/// URLs only, and never writes <c>KeeperLink.Reveal</c>.
/// </summary>
public static class DocuEngAIneMcpServer
{
    public const string ProtocolVersion = "2025-06-18";
    public const string ServerName = "DocuEngAIne";
    public const string ServerVersion = "1.0.0";

    public const string ListCompanies = "list_companies";
    public const string GetCompany = "get_company";
    public const string ListAssets = "list_assets";
    public const string GetAsset = "get_asset";
    public const string ListDocuments = "list_documents";
    public const string ListRunbooks = "list_runbooks";
    public const string ListExpirations = "list_expirations";
    public const string ListKeeperLinks = "list_keeper_links";

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    public static IReadOnlyList<McpToolDefinition> Tools { get; } =
    [
        new(ListCompanies, "List companies (clients) in the authenticated tenant.", new
        {
            type = "object",
            properties = new
            {
                q = new { type = "string", description = "Optional name / slug / Halo / Ninja search." },
            },
        }),
        new(GetCompany, "Get one company by id. Other-tenant or unknown ids are not found.", new
        {
            type = "object",
            required = new[] { "companyId" },
            properties = new
            {
                companyId = new { type = "string", description = "Company id (GUID)." },
            },
        }),
        new(ListAssets, "List assets in the authenticated tenant.", new
        {
            type = "object",
            properties = new
            {
                companyId = new { type = "string", description = "Optional company filter. Other-tenant ids yield an empty list." },
                q = new { type = "string", description = "Optional name search." },
            },
        }),
        new(GetAsset, "Get one asset by id, including custom fields. Other-tenant or unknown ids are not found.", new
        {
            type = "object",
            required = new[] { "assetId" },
            properties = new
            {
                assetId = new { type = "string", description = "Asset id (GUID)." },
            },
        }),
        new(ListDocuments, "List published documents in the authenticated tenant.", new
        {
            type = "object",
            properties = new
            {
                companyId = new { type = "string", description = "Optional company filter. Other-tenant ids yield an empty list." },
                search = new { type = "string", description = "Optional title / summary / tag search." },
                folderId = new { type = "string", description = "Optional folder filter. Other-tenant ids yield an empty list." },
            },
        }),
        new(ListRunbooks, "List published runbooks in the authenticated tenant.", new
        {
            type = "object",
            properties = new
            {
                companyId = new { type = "string", description = "Optional company filter. Other-tenant ids yield an empty list." },
                search = new { type = "string", description = "Optional title / description / tag search." },
            },
        }),
        new(ListExpirations, "List asset expirations in the authenticated tenant.", new
        {
            type = "object",
            properties = new
            {
                companyId = new { type = "string", description = "Optional company filter. Other-tenant ids yield an empty list." },
                showExpired = new { type = "boolean", description = "Include past dates. Default false." },
                q = new { type = "string", description = "Optional name / company / field search." },
            },
        }),
        new(ListKeeperLinks, "List Keeper links (titles and record URLs only). Does not reveal or audit-log a reveal.", new
        {
            type = "object",
            properties = new
            {
                companyId = new { type = "string", description = "Optional company filter. Other-tenant ids yield an empty list." },
            },
        }),
    ];

    public static bool IsKnownTool(string? name) =>
        !string.IsNullOrWhiteSpace(name) && Tools.Any(t => t.Name == name);

    public static bool IsNotification(string? method, bool hasId) =>
        !hasId && method is not null && method.StartsWith("notifications/", StringComparison.Ordinal);

    public static McpJsonRpcResponse Initialize(object? id) =>
        Ok(id, new
        {
            protocolVersion = ProtocolVersion,
            capabilities = new { tools = new { listChanged = false } },
            serverInfo = new { name = ServerName, version = ServerVersion },
            instructions = "Read-only DocuEngAIne documentation for one tenant. Authenticate with a per-tenant API token (Authorization: Bearer). Every query is scoped to that tenant. Keeper reveal is not a tool.",
        });

    public static McpJsonRpcResponse ListTools(object? id) =>
        Ok(id, new { tools = Tools.Select(t => new { name = t.Name, description = t.Description, inputSchema = t.InputSchema }) });

    public static async Task<McpJsonRpcResponse> HandleAsync(
        McpJsonRpcRequest request,
        DocuEngAIneDbContext db,
        ICurrentUser user,
        CancellationToken cancellationToken = default)
    {
        if (IsNotification(request.Method, request.HasId))
            return new McpJsonRpcResponse { Id = null, Result = null };

        if (string.IsNullOrWhiteSpace(request.Method))
            return Error(request.Id, -32600, "Invalid Request: method is required.");

        return request.Method switch
        {
            "initialize" => Initialize(request.Id),
            "ping" => Ok(request.Id, new { }),
            "tools/list" => ListTools(request.Id),
            "tools/call" => await CallToolRpcAsync(request, db, user, cancellationToken),
            _ => Error(request.Id, -32601, $"Method not found: {request.Method}"),
        };
    }

    private static async Task<McpJsonRpcResponse> CallToolRpcAsync(
        McpJsonRpcRequest request,
        DocuEngAIneDbContext db,
        ICurrentUser user,
        CancellationToken cancellationToken)
    {
        if (request.Params is not { } p || p.ValueKind != JsonValueKind.Object)
            return Error(request.Id, -32602, "tools/call requires params.name.");

        if (!p.TryGetProperty("name", out var nameEl) || nameEl.ValueKind != JsonValueKind.String)
            return Error(request.Id, -32602, "tools/call requires params.name.");

        var name = nameEl.GetString();
        if (!IsKnownTool(name))
            return Error(request.Id, -32601, $"Tool not found: {name}");

        JsonElement? arguments = null;
        if (p.TryGetProperty("arguments", out var args) && args.ValueKind is JsonValueKind.Object or JsonValueKind.Null)
            arguments = args.ValueKind == JsonValueKind.Null ? null : args;

        try
        {
            var payload = await InvokeToolAsync(name!, arguments, db, user, cancellationToken);
            return Ok(request.Id, ToolResult(payload, isError: false));
        }
        catch (McpToolException ex)
        {
            return Ok(request.Id, ToolResult(new { error = ex.Message }, isError: true));
        }
    }

    public static async Task<object> InvokeToolAsync(
        string name,
        JsonElement? arguments,
        DocuEngAIneDbContext db,
        ICurrentUser user,
        CancellationToken cancellationToken = default)
    {
        return name switch
        {
            ListCompanies => await ListCompaniesAsync(arguments, db, user, cancellationToken),
            GetCompany => await GetCompanyAsync(arguments, db, user, cancellationToken),
            ListAssets => await ListAssetsAsync(arguments, db, user, cancellationToken),
            GetAsset => await GetAssetAsync(arguments, db, user, cancellationToken),
            ListDocuments => await ListDocumentsAsync(arguments, db, user, cancellationToken),
            ListRunbooks => await ListRunbooksAsync(arguments, db, user, cancellationToken),
            ListExpirations => await ListExpirationsAsync(arguments, db, user, cancellationToken),
            ListKeeperLinks => await ListKeeperLinksAsync(arguments, db, user, cancellationToken),
            _ => throw new McpToolException($"Tool not found: {name}"),
        };
    }

    private static async Task<object> ListCompaniesAsync(
        JsonElement? arguments,
        DocuEngAIneDbContext db,
        ICurrentUser user,
        CancellationToken cancellationToken)
    {
        var q = ReadString(arguments, "q");
        var query = db.Companies.ForTenant(user).AsNoTracking();
        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = q.Trim();
            query = query.Where(c =>
                c.Name.Contains(term)
                || c.Slug.Contains(term)
                || (c.HaloClientId != null && c.HaloClientId.Contains(term))
                || (c.NinjaOrganizationId != null && c.NinjaOrganizationId.Contains(term)));
        }

        var items = await query.OrderBy(c => c.Name)
            .Select(c => new
            {
                c.Id,
                c.Name,
                c.Slug,
                c.CompanyType,
                c.Nickname,
                c.PrimaryDomain,
                c.IsActive,
                c.HaloClientId,
                c.NinjaOrganizationId,
                c.HaloPortalUrl,
                c.NinjaPortalUrl,
                c.UpdatedAt,
            })
            .ToListAsync(cancellationToken);
        return items;
    }

    private static async Task<object> GetCompanyAsync(
        JsonElement? arguments,
        DocuEngAIneDbContext db,
        ICurrentUser user,
        CancellationToken cancellationToken)
    {
        var companyId = ReadGuid(arguments, "companyId")
            ?? throw new McpToolException("companyId is required.");

        var company = await db.Companies.ForTenant(user).AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == companyId, cancellationToken);
        if (company is null)
            throw new McpToolException("Company not found.");

        var related = await CompanyEndpoints.LoadRelatedAsync(db, user, company.Id, CompanyEndpoints.RelatedTake, cancellationToken);
        return new
        {
            company.Id,
            company.Name,
            company.Slug,
            company.CompanyNumber,
            company.CompanyType,
            company.Nickname,
            company.ParentCompanyId,
            company.PrimaryDomain,
            company.Address,
            company.City,
            company.State,
            company.Country,
            company.PostalCode,
            company.Phone,
            company.Fax,
            company.Website,
            company.Notes,
            company.HoursOfOperation,
            company.IsActive,
            company.PortalEnabled,
            company.HaloClientId,
            company.NinjaOrganizationId,
            company.HaloPortalUrl,
            company.NinjaPortalUrl,
            company.CreatedAt,
            company.UpdatedAt,
            Counts = new
            {
                Assets = related.AssetCount,
                Documents = related.DocumentCount,
                Runbooks = related.RunbookCount,
                KeeperLinks = related.KeeperLinkCount,
                RelatedLinks = related.RelatedLinkCount,
            },
            related.Assets,
            related.Documents,
            related.Runbooks,
            related.KeeperLinks,
            related.RelatedLinks,
        };
    }

    private static async Task<object> ListAssetsAsync(
        JsonElement? arguments,
        DocuEngAIneDbContext db,
        ICurrentUser user,
        CancellationToken cancellationToken)
    {
        if (await RejectForeignCompanyAsync(arguments, db, user, cancellationToken))
            return Array.Empty<object>();

        var companyId = ReadGuid(arguments, "companyId");
        var q = ReadString(arguments, "q");
        var query = db.Assets.ForTenant(user).AsNoTracking().Include(a => a.AssetType).AsQueryable();
        if (companyId is Guid cid)
            query = query.Where(a => a.CompanyId == cid);
        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = q.Trim();
            query = query.Where(a => a.Name.Contains(term));
        }

        var items = await query.OrderBy(a => a.Name)
            .Select(a => new
            {
                a.Id,
                a.Name,
                a.Location,
                a.Status,
                a.CompanyId,
                a.ExpiresAt,
                a.HaloAssetUrl,
                a.NinjaDeviceUrl,
                AssetType = a.AssetType != null ? a.AssetType.Name : null,
                a.UpdatedAt,
            })
            .ToListAsync(cancellationToken);
        return items;
    }

    private static async Task<object> GetAssetAsync(
        JsonElement? arguments,
        DocuEngAIneDbContext db,
        ICurrentUser user,
        CancellationToken cancellationToken)
    {
        var assetId = ReadGuid(arguments, "assetId")
            ?? throw new McpToolException("assetId is required.");

        var asset = await db.Assets.ForTenant(user).AsNoTracking()
            .Include(a => a.AssetType)
                .ThenInclude(t => t!.Fields)
            .Include(a => a.CustomFieldValues)
                .ThenInclude(v => v.FieldDefinition)
            .FirstOrDefaultAsync(a => a.Id == assetId, cancellationToken);
        if (asset is null)
            throw new McpToolException("Asset not found.");

        return new
        {
            asset.Id,
            asset.Name,
            asset.Location,
            asset.Status,
            asset.Notes,
            asset.CompanyId,
            asset.ExpiresAt,
            asset.HaloAssetUrl,
            asset.NinjaDeviceUrl,
            asset.ExternalIdsJson,
            AssetType = new { asset.AssetType?.Id, asset.AssetType?.Name },
            Fields = asset.CustomFieldValues.Select(v => new
            {
                v.FieldDefinition?.Name,
                v.FieldDefinition?.FieldType,
                v.Value,
            }),
            asset.UpdatedAt,
        };
    }

    private static async Task<object> ListDocumentsAsync(
        JsonElement? arguments,
        DocuEngAIneDbContext db,
        ICurrentUser user,
        CancellationToken cancellationToken)
    {
        if (await RejectForeignCompanyAsync(arguments, db, user, cancellationToken))
            return Array.Empty<object>();

        var companyId = ReadGuid(arguments, "companyId");
        var search = ReadString(arguments, "search");
        var folderId = ReadGuid(arguments, "folderId");
        var docs = await DocumentEndpoints.ListAsync(db, user, search, folderId, cancellationToken);
        if (companyId is Guid cid)
            docs = docs.Where(d => d.CompanyId == cid).ToList();
        return docs;
    }

    private static async Task<object> ListRunbooksAsync(
        JsonElement? arguments,
        DocuEngAIneDbContext db,
        ICurrentUser user,
        CancellationToken cancellationToken)
    {
        if (await RejectForeignCompanyAsync(arguments, db, user, cancellationToken))
            return Array.Empty<object>();

        var companyId = ReadGuid(arguments, "companyId");
        var search = ReadString(arguments, "search");
        var runbooks = await RunbookEndpoints.ListPublishedAsync(db, user, search, cancellationToken);
        if (companyId is Guid cid)
            runbooks = runbooks.Where(r => r.CompanyId == cid).ToList();
        return runbooks;
    }

    private static async Task<object> ListExpirationsAsync(
        JsonElement? arguments,
        DocuEngAIneDbContext db,
        ICurrentUser user,
        CancellationToken cancellationToken)
    {
        var companyId = ReadGuid(arguments, "companyId");
        var showExpired = ReadBool(arguments, "showExpired") ?? false;
        var q = ReadString(arguments, "q");
        return await ExpirationEndpoints.QueryAsync(db, user, companyId, showExpired, q, cancellationToken);
    }

    private static async Task<object> ListKeeperLinksAsync(
        JsonElement? arguments,
        DocuEngAIneDbContext db,
        ICurrentUser user,
        CancellationToken cancellationToken)
    {
        if (await RejectForeignCompanyAsync(arguments, db, user, cancellationToken))
            return Array.Empty<object>();

        var companyId = ReadGuid(arguments, "companyId");
        var query = db.KeeperLinks.ForTenant(user).AsNoTracking();
        if (companyId is Guid cid)
            query = query.Where(k => k.CompanyId == cid);

        // Titles and URLs only. Notes / username hints stay off this surface. This is not a reveal:
        // the HTTP reveal path is POST /api/keeper/{id}/reveal and writes KeeperLink.Reveal.
        var items = await query.OrderBy(k => k.Name)
            .Select(k => new
            {
                k.Id,
                Title = k.Name,
                Url = k.KeeperRecordUrl,
                k.CompanyId,
            })
            .ToListAsync(cancellationToken);
        return items;
    }

    /// <summary>
    /// Other-tenant / unknown company filters yield empty, never a cross-tenant row. Matches
    /// <see cref="ExpirationEndpoints"/> — a foreign id is not a 500 and is not a leak.
    /// </summary>
    private static async Task<bool> RejectForeignCompanyAsync(
        JsonElement? arguments,
        DocuEngAIneDbContext db,
        ICurrentUser user,
        CancellationToken cancellationToken)
    {
        if (ReadGuid(arguments, "companyId") is not Guid cid)
            return false;

        return !await db.Companies.ForTenant(user).AsNoTracking().AnyAsync(c => c.Id == cid, cancellationToken);
    }

    private static object ToolResult(object payload, bool isError) => new
    {
        content = new object[]
        {
            new { type = "text", text = JsonSerializer.Serialize(payload, JsonOptions) },
        },
        isError,
    };

    public static McpJsonRpcResponse Ok(object? id, object result) =>
        new() { Id = id, Result = result };

    public static McpJsonRpcResponse Error(object? id, int code, string message) =>
        new() { Id = id, Error = new { code, message } };

    public static McpJsonRpcRequest Parse(JsonElement root)
    {
        string? method = null;
        if (root.TryGetProperty("method", out var methodEl) && methodEl.ValueKind == JsonValueKind.String)
            method = methodEl.GetString();

        object? id = null;
        var hasId = root.TryGetProperty("id", out var idEl) && idEl.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined;
        if (hasId)
            id = UnwrapId(idEl);

        JsonElement? @params = null;
        if (root.TryGetProperty("params", out var paramsEl))
            @params = paramsEl;

        return new McpJsonRpcRequest("2.0", id, hasId, method, @params);
    }

    private static object? UnwrapId(JsonElement id) => id.ValueKind switch
    {
        JsonValueKind.String => id.GetString(),
        JsonValueKind.Number when id.TryGetInt64(out var n) => n,
        JsonValueKind.Number => id.GetRawText(),
        _ => id.GetRawText(),
    };

    private static string? ReadString(JsonElement? arguments, string name)
    {
        if (arguments is not { } el || el.ValueKind != JsonValueKind.Object)
            return null;
        if (!TryGetPropertyIgnoreCase(el, name, out var p))
            return null;
        return p.ValueKind switch
        {
            JsonValueKind.String => p.GetString(),
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => p.GetRawText(),
        };
    }

    private static Guid? ReadGuid(JsonElement? arguments, string name)
    {
        var raw = ReadString(arguments, name);
        return Guid.TryParse(raw, out var id) ? id : null;
    }

    private static bool? ReadBool(JsonElement? arguments, string name)
    {
        if (arguments is not { } el || el.ValueKind != JsonValueKind.Object)
            return null;
        if (!TryGetPropertyIgnoreCase(el, name, out var p))
            return null;
        return p.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(p.GetString(), out var b) => b,
            _ => null,
        };
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement obj, string name, out JsonElement value)
    {
        if (obj.TryGetProperty(name, out value))
            return true;

        foreach (var prop in obj.EnumerateObject())
        {
            if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = prop.Value;
                return true;
            }
        }

        value = default;
        return false;
    }
}

public sealed record McpToolDefinition(string Name, string Description, object InputSchema);

public sealed record McpJsonRpcRequest(
    string Jsonrpc,
    object? Id,
    bool HasId,
    string? Method,
    JsonElement? Params);

public sealed class McpJsonRpcResponse
{
    [JsonPropertyName("jsonrpc")]
    public string Jsonrpc { get; init; } = "2.0";

    [JsonPropertyName("id")]
    public object? Id { get; init; }

    [JsonPropertyName("result")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? Result { get; init; }

    [JsonPropertyName("error")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? Error { get; init; }
}

public sealed class McpToolException : Exception
{
    public McpToolException(string message) : base(message)
    {
    }
}
