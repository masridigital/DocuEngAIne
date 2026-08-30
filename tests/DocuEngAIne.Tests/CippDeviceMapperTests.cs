using System.Text.Json;
using DocuEngAIne.Core.Interfaces;
using DocuEngAIne.Infrastructure.Integrations;

namespace DocuEngAIne.Tests;

public class CippDeviceMapperTests
{
    public const string AdrocCustomerId = "8c65106e-9e7e-45d4-b55a-3cbd4b415a08";
    public const string AdrocTenantFilter = "adroccap.com";

    // Fixture-only Compact cipp_list_devices JSON array (Graph managedDevice field names exact).
    // Not a live pull. Adroc customerId matches CippTenantMapperTests.LiveCompactListFixture.
    public const string DeviceListFixture = """
        [{"id":"3f2a1c80-9b4e-4d11-8c6a-2e7f0b1d4a55","deviceName":"ADROC-LAPTOP-01","operatingSystem":"Windows","complianceState":"compliant","lastSyncDateTime":"2026-08-29T18:12:00Z"},{"id":"9c8b7a66-5544-4333-2211-00ffeeddccbb","deviceName":"ADROC-IPHONE-12","operatingSystem":"iOS","complianceState":"noncompliant","lastSyncDateTime":"2026-08-28T09:00:00Z"}]
        """;

    [Fact]
    public void MapDevices_Fixture_MapsIdNameOs_StampsCustomerId()
    {
        var devices = CippDeviceMapper.MapDevices(DeviceListFixture, AdrocCustomerId);

        Assert.Equal(2, devices.Count);

        var laptop = devices[0];
        Assert.Equal("3f2a1c80-9b4e-4d11-8c6a-2e7f0b1d4a55", laptop.ExternalId);
        Assert.Equal(AdrocCustomerId, laptop.OrganizationExternalId);
        Assert.Equal("ADROC-LAPTOP-01", laptop.Name);
        Assert.Equal("Windows", laptop.NodeClass);
        Assert.Null(laptop.SystemName);
        Assert.Null(laptop.DnsName);

        var phone = devices[1];
        Assert.Equal("9c8b7a66-5544-4333-2211-00ffeeddccbb", phone.ExternalId);
        Assert.Equal(AdrocCustomerId, phone.OrganizationExternalId);
        Assert.Equal("ADROC-IPHONE-12", phone.Name);
        Assert.Equal("iOS", phone.NodeClass);
    }

    [Fact]
    public void MapDevices_JsonRpcContentTextArray_UnwrapsToDeviceList()
    {
        var wrapped = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = "1",
            result = new { content = new[] { new { type = "text", text = DeviceListFixture } } },
        });

        var devices = CippDeviceMapper.MapDevices(wrapped, AdrocCustomerId);
        Assert.Equal(2, devices.Count);
        Assert.Equal("3f2a1c80-9b4e-4d11-8c6a-2e7f0b1d4a55", devices[0].ExternalId);
        Assert.Equal("ADROC-LAPTOP-01", devices[0].Name);
        Assert.Equal("Windows", devices[0].NodeClass);
        Assert.Equal(AdrocCustomerId, devices[0].OrganizationExternalId);
    }

    [Fact]
    public void MapDevices_DeviceNameWinsOverDisplayNameAndName()
    {
        const string json = """
            [{"id":"3f2a1c80-9b4e-4d11-8c6a-2e7f0b1d4a55","deviceName":"ADROC-LAPTOP-01","displayName":"Reception","name":"old-name","operatingSystem":"Windows"}]
            """;

        var device = Assert.Single(CippDeviceMapper.MapDevices(json, AdrocCustomerId));
        Assert.Equal("ADROC-LAPTOP-01", device.Name);
    }

    [Fact]
    public void MapDevices_DropsRowsWithoutIdOrName()
    {
        const string json = """
            [{"deviceName":"NO-ID","operatingSystem":"Windows"},{"id":"aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"},{"id":"3f2a1c80-9b4e-4d11-8c6a-2e7f0b1d4a55","deviceName":"ADROC-LAPTOP-01","operatingSystem":"Windows"}]
            """;

        var device = Assert.Single(CippDeviceMapper.MapDevices(json, AdrocCustomerId));
        Assert.Equal("3f2a1c80-9b4e-4d11-8c6a-2e7f0b1d4a55", device.ExternalId);
        Assert.Equal("ADROC-LAPTOP-01", device.Name);
    }

    [Fact]
    public void MapDevices_DoesNotSkipExcludedOrPartnerFields()
    {
        const string json = """
            [{"id":"3f2a1c80-9b4e-4d11-8c6a-2e7f0b1d4a55","deviceName":"ADROC-LAPTOP-01","operatingSystem":"Windows","Excluded":true,"domains":"PartnerTenant","displayName":"*Partner Tenant"}]
            """;

        var device = Assert.Single(CippDeviceMapper.MapDevices(json, AdrocCustomerId));
        Assert.Equal("ADROC-LAPTOP-01", device.Name);
        Assert.Equal(AdrocCustomerId, device.OrganizationExternalId);
    }

    [Fact]
    public void MapDevices_EmptyOrganizationExternalId_MapsNothing()
    {
        Assert.Empty(CippDeviceMapper.MapDevices(DeviceListFixture, ""));
        Assert.Empty(CippDeviceMapper.MapDevices(DeviceListFixture, "   "));
    }

    [Fact]
    public void MapDevices_ToolError_Throws()
    {
        var body = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = "1",
            error = new { code = -32000, message = "cipp auth expired" },
        });

        var ex = Assert.Throws<InvalidOperationException>(() =>
        {
            CippDeviceMapper.MapDevices(body, AdrocCustomerId);
        });
        Assert.Contains("cipp auth expired", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildArgumentsJson_PassesTenantFilter_NoPagination()
    {
        var args = CippDeviceMapper.BuildArgumentsJson(AdrocTenantFilter);
        Assert.Contains("\"tenantFilter\":\"adroccap.com\"", args, StringComparison.Ordinal);
        Assert.DoesNotContain("tenantsOnly", args, StringComparison.Ordinal);
        Assert.DoesNotContain("ClearCache", args, StringComparison.Ordinal);
        Assert.DoesNotContain("pageSize", args, StringComparison.Ordinal);
        Assert.DoesNotContain("pageNo", args, StringComparison.Ordinal);
        Assert.DoesNotContain("customerId", args, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PullAsync_Calls_CippListDevices_WithTenantFilter()
    {
        var mcp = new ScriptedMcp();
        var serverId = Guid.NewGuid();

        var devices = await CippDeviceMapper.PullAsync(mcp, serverId, AdrocTenantFilter, AdrocCustomerId);

        Assert.Equal(2, devices.Count);
        Assert.Equal("ADROC-LAPTOP-01", devices[0].Name);
        Assert.Equal(AdrocCustomerId, devices[0].OrganizationExternalId);
        var call = Assert.Single(mcp.Calls);
        Assert.Equal(CippDeviceMapper.ToolName, call.Tool);
        Assert.Equal(serverId, call.ServerId);
        Assert.Contains("\"tenantFilter\":\"adroccap.com\"", call.Args, StringComparison.Ordinal);
        Assert.DoesNotContain("pageSize", call.Args, StringComparison.Ordinal);
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
                result = new { content = new[] { new { type = "text", text = DeviceListFixture } } },
            });
            return Task.FromResult(body);
        }
    }
}
