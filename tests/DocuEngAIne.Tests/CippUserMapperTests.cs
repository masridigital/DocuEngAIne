using System.Text.Json;
using DocuEngAIne.Core.Entities;
using DocuEngAIne.Core.Interfaces;
using DocuEngAIne.Infrastructure.Integrations;

namespace DocuEngAIne.Tests;

public class CippUserMapperTests
{
    public const string AdrocCustomerId = "8c65106e-9e7e-45d4-b55a-3cbd4b415a08";
    public const string AdrocTenantFilter = "adroccap.com";

    // Fixture-only Compact cipp_list_users JSON array (Graph user field names exact).
    // Not a live pull. Adroc customerId matches CippTenantMapperTests.LiveCompactListFixture.
    public const string UserListFixture = """
        [{"id":"11111111-2222-3333-4444-555555555555","displayName":"James Adroc","userPrincipalName":"james@adroccap.com","accountEnabled":true,"assignedLicenses":[{"skuId":"6fd2c87f-b296-42f0-b197-1e91e994b900"}],"LicJoined":"Microsoft 365 Business Premium"},{"id":"aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee","displayName":"Disabled User","userPrincipalName":"disabled@adroccap.com","accountEnabled":false,"assignedLicenses":[],"LicJoined":""}]
        """;

    [Fact]
    public void MapUsers_Fixture_MapsIdUpnName_StampsCustomerId_NotUserEntity()
    {
        var contacts = CippUserMapper.MapUsers(UserListFixture, AdrocCustomerId);

        Assert.Equal(2, contacts.Count);
        Assert.All(contacts, c => Assert.IsType<ExternalContactDto>(c));
        Assert.All(contacts, c => Assert.IsNotType<User>(c));

        var james = contacts[0];
        Assert.Equal("11111111-2222-3333-4444-555555555555", james.ExternalId);
        Assert.Equal(AdrocCustomerId, james.ClientExternalId);
        Assert.Equal("James Adroc", james.Name);
        Assert.Equal("james@adroccap.com", james.Email);
        Assert.Null(james.SiteExternalId);
        Assert.False(james.IsInactive);

        var disabled = contacts[1];
        Assert.Equal("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", disabled.ExternalId);
        Assert.Equal(AdrocCustomerId, disabled.ClientExternalId);
        Assert.Equal("Disabled User", disabled.Name);
        Assert.Equal("disabled@adroccap.com", disabled.Email);
        Assert.Null(disabled.SiteExternalId);
        Assert.True(disabled.IsInactive);
    }

    [Fact]
    public void MapUsers_JsonRpcContentTextArray_UnwrapsToUserList()
    {
        var wrapped = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = "1",
            result = new { content = new[] { new { type = "text", text = UserListFixture } } },
        });

        var contacts = CippUserMapper.MapUsers(wrapped, AdrocCustomerId);
        Assert.Equal(2, contacts.Count);
        Assert.Equal("11111111-2222-3333-4444-555555555555", contacts[0].ExternalId);
        Assert.Equal("James Adroc", contacts[0].Name);
        Assert.Equal("james@adroccap.com", contacts[0].Email);
        Assert.Equal(AdrocCustomerId, contacts[0].ClientExternalId);
    }

    [Fact]
    public void MapUsers_DisplayNameWinsOverName()
    {
        const string json = """
            [{"id":"11111111-2222-3333-4444-555555555555","displayName":"James Adroc","name":"old-name","userPrincipalName":"james@adroccap.com","accountEnabled":true}]
            """;

        var contact = Assert.Single(CippUserMapper.MapUsers(json, AdrocCustomerId));
        Assert.Equal("James Adroc", contact.Name);
    }

    [Fact]
    public void MapUsers_DropsRowsWithoutIdUpnOrName()
    {
        const string json = """
            [{"displayName":"NO-ID","userPrincipalName":"noid@adroccap.com"},{"id":"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa","displayName":"NO-UPN"},{"id":"bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb","userPrincipalName":"noname@adroccap.com"},{"id":"11111111-2222-3333-4444-555555555555","displayName":"James Adroc","userPrincipalName":"james@adroccap.com","accountEnabled":true}]
            """;

        var contact = Assert.Single(CippUserMapper.MapUsers(json, AdrocCustomerId));
        Assert.Equal("11111111-2222-3333-4444-555555555555", contact.ExternalId);
        Assert.Equal("James Adroc", contact.Name);
        Assert.Equal("james@adroccap.com", contact.Email);
    }

    [Fact]
    public void MapUsers_DoesNotSkipExcludedOrPartnerFields()
    {
        const string json = """
            [{"id":"11111111-2222-3333-4444-555555555555","displayName":"James Adroc","userPrincipalName":"james@adroccap.com","accountEnabled":true,"Excluded":true,"domains":"PartnerTenant"}]
            """;

        var contact = Assert.Single(CippUserMapper.MapUsers(json, AdrocCustomerId));
        Assert.Equal("James Adroc", contact.Name);
        Assert.Equal(AdrocCustomerId, contact.ClientExternalId);
    }

    [Fact]
    public void MapUsers_EmptyClientExternalId_MapsNothing()
    {
        Assert.Empty(CippUserMapper.MapUsers(UserListFixture, ""));
        Assert.Empty(CippUserMapper.MapUsers(UserListFixture, "   "));
    }

    [Fact]
    public void MapUsers_DoesNotMapLicenseStatus()
    {
        var james = Assert.Single(CippUserMapper.MapUsers(UserListFixture, AdrocCustomerId), c => c.Email == "james@adroccap.com");

        Assert.Null(typeof(ExternalContactDto).GetProperty("LicJoined"));
        Assert.Null(typeof(ExternalContactDto).GetProperty("AssignedLicenses"));
        Assert.Null(typeof(ExternalContactDto).GetProperty("LicenseStatus"));
        Assert.Equal("James Adroc", james.Name);
    }

    [Fact]
    public void MapUsers_ToolError_Throws()
    {
        var body = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = "1",
            error = new { code = -32000, message = "cipp auth expired" },
        });

        var ex = Assert.Throws<InvalidOperationException>(() =>
        {
            CippUserMapper.MapUsers(body, AdrocCustomerId);
        });
        Assert.Contains("cipp auth expired", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildArgumentsJson_PassesTenantFilter_NoPagination()
    {
        var args = CippUserMapper.BuildArgumentsJson(AdrocTenantFilter);
        Assert.Contains("\"tenantFilter\":\"adroccap.com\"", args, StringComparison.Ordinal);
        Assert.DoesNotContain("tenantsOnly", args, StringComparison.Ordinal);
        Assert.DoesNotContain("ClearCache", args, StringComparison.Ordinal);
        Assert.DoesNotContain("pageSize", args, StringComparison.Ordinal);
        Assert.DoesNotContain("pageNo", args, StringComparison.Ordinal);
        Assert.DoesNotContain("customerId", args, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PullAsync_Calls_CippListUsers_WithTenantFilter()
    {
        var mcp = new ScriptedMcp();
        var serverId = Guid.NewGuid();

        var contacts = await CippUserMapper.PullAsync(mcp, serverId, AdrocTenantFilter, AdrocCustomerId);

        Assert.Equal(2, contacts.Count);
        Assert.Equal("James Adroc", contacts[0].Name);
        Assert.Equal(AdrocCustomerId, contacts[0].ClientExternalId);
        Assert.All(contacts, c => Assert.IsType<ExternalContactDto>(c));
        var call = Assert.Single(mcp.Calls);
        Assert.Equal(CippUserMapper.ToolName, call.Tool);
        Assert.Equal(serverId, call.ServerId);
        Assert.Contains("\"tenantFilter\":\"adroccap.com\"", call.Args, StringComparison.Ordinal);
        Assert.DoesNotContain("pageSize", call.Args, StringComparison.Ordinal);
        Assert.DoesNotContain("cipp_list_tenants", mcp.Calls.Select(c => c.Tool));
        Assert.DoesNotContain("cipp_get_user", mcp.Calls.Select(c => c.Tool));
    }

    private sealed class ScriptedMcp : IMcpClient
    {
        public List<(Guid ServerId, string Tool, string? Args)> Calls { get; } = [];

        public Task<string> ListToolsAsync(Guid mcpServerId, CancellationToken cancellationToken = default)
            => Task.FromResult("""{"result":{"tools":[]}}""");

        public Task<string> CallToolAsync(Guid mcpServerId, string toolName, string? argumentsJson, CancellationToken cancellationToken = default)
        {
            Calls.Add((mcpServerId, toolName, argumentsJson));
            var body = JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = "1",
                result = new { content = new[] { new { type = "text", text = UserListFixture } } },
            });
            return Task.FromResult(body);
        }
    }
}
