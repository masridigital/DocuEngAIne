using System.Text.Json;
using DocuEngAIne.Core.Interfaces;
using DocuEngAIne.Infrastructure.Integrations;

namespace DocuEngAIne.Tests;

public class UnifiDeviceMapperTests
{
    // Representative Compact unifi_sm_list_devices wrapper (account-wide inventory grouped by host).
    // Field names match the Compact envelope; values are fixtures, not a live pull.
    public const string CompactListFixture = """
        {"data":[{"hostId":"host-1","hostName":"Adroc Capital: 1425 RXR Plaza","devices":[{"id":"dev-ap-1","mac":"aa:bb:cc:dd:ee:01","name":"Office AP","model":"U7-Pro","ip":"192.168.1.10","productLine":"network","status":"online","version":"7.0.1","isConsole":false,"isManaged":true},{"id":"dev-udm-1","mac":"aa:bb:cc:dd:ee:02","name":"UDM Pro","model":"UDM-Pro","ip":"192.168.1.1","productLine":"network","status":"online","version":"4.1.0","isConsole":true,"isManaged":true}]},{"hostId":"host-2","hostName":"Blocked Co","devices":[{"id":"dev-sw-1","mac":"aa:bb:cc:dd:ee:03","name":"Core Switch","model":"USW-24-PoE","ip":"10.0.0.2","productLine":"network","status":"offline","version":"6.6.0","isConsole":false,"isManaged":true}]}],"httpStatusCode":200}
        """;

    // Hand-built: rows the representative sample never contains.
    private const string DegenerateDeviceListFixture = """
        {"data":[{"hostId":"host-1","hostName":"Adroc","devices":[{"mac":"aa:bb:cc:dd:ee:99","name":"NO-ID"},{"id":"dev-ok","mac":"aa:bb:cc:dd:ee:04","name":"OK AP","productLine":"network","ip":"192.168.1.20"},{"id":"dev-mac-only","mac":"aa:bb:cc:dd:ee:05"}]},{"hostName":"No Host Id","devices":[{"id":"dev-orphan","mac":"aa:bb:cc:dd:ee:06","name":"Orphan"}]}]}
        """;

    [Fact]
    public void ToolName_IsAccountWideInventory_NotSiteScopedNetList()
    {
        Assert.Equal("unifi_sm_list_devices", UnifiDeviceMapper.ToolName);
        Assert.NotEqual("unifi_net_list_devices", UnifiDeviceMapper.ToolName);
    }

    [Fact]
    public void MapDevices_CompactList_MapsIdNameAndHostAsOrganization()
    {
        var devices = UnifiDeviceMapper.MapDevices(CompactListFixture);

        Assert.Equal(3, devices.Count);

        var ap = devices[0];
        Assert.Equal("dev-ap-1", ap.ExternalId);
        Assert.Equal("host-1", ap.OrganizationExternalId);
        Assert.Equal("Office AP", ap.Name);
        Assert.Equal("network", ap.NodeClass);
        Assert.Equal("aa:bb:cc:dd:ee:01", ap.SystemName);
        Assert.Equal("192.168.1.10", ap.DnsName);

        Assert.Equal("dev-udm-1", devices[1].ExternalId);
        Assert.Equal("host-1", devices[1].OrganizationExternalId);
        Assert.Equal("UDM Pro", devices[1].Name);

        var sw = devices[2];
        Assert.Equal("dev-sw-1", sw.ExternalId);
        Assert.Equal("host-2", sw.OrganizationExternalId);
        Assert.Equal("Core Switch", sw.Name);
        Assert.Equal("aa:bb:cc:dd:ee:03", sw.SystemName);
        Assert.Equal("10.0.0.2", sw.DnsName);
    }

    [Fact]
    public void MapDevices_FallsBackToMac_WhenNameEmpty()
    {
        var devices = UnifiDeviceMapper.MapDevices(DegenerateDeviceListFixture);
        var macOnly = Assert.Single(devices, d => d.ExternalId == "dev-mac-only");
        Assert.Equal("aa:bb:cc:dd:ee:05", macOnly.Name);
        Assert.Equal("aa:bb:cc:dd:ee:05", macOnly.SystemName);
        Assert.Equal("host-1", macOnly.OrganizationExternalId);
    }

    [Fact]
    public void MapDevices_SkipsMissingIdAndHostId()
    {
        var devices = UnifiDeviceMapper.MapDevices(DegenerateDeviceListFixture, out _, out var dataCount);

        Assert.Equal(2, dataCount);
        Assert.Equal(2, devices.Count);
        Assert.DoesNotContain(devices, d => d.Name == "NO-ID");
        Assert.DoesNotContain(devices, d => d.ExternalId == "dev-orphan");
        Assert.Contains(devices, d => d.ExternalId == "dev-ok");
        Assert.Contains(devices, d => d.ExternalId == "dev-mac-only");
    }

    [Fact]
    public void MapDevices_DoesNotStoreFirmwareBlobs()
    {
        const string json = """
            {"data":[{"hostId":"host-1","hostName":"Adroc","devices":[{"id":"dev-1","mac":"aa:bb:cc:dd:ee:01","name":"Office AP","version":"7.0.1","firmwareStatus":"upToDate","updateAvailable":false,"firmware":{"blob":"DO-NOT-STORE-FIRMWARE","image":"very-large-payload"}}]}]}
            """;

        var device = Assert.Single(UnifiDeviceMapper.MapDevices(json));
        Assert.Equal("dev-1", device.ExternalId);
        Assert.Equal("Office AP", device.Name);
        Assert.Equal("host-1", device.OrganizationExternalId);
        Assert.Equal("aa:bb:cc:dd:ee:01", device.SystemName);

        var serialized = JsonSerializer.Serialize(device);
        Assert.DoesNotContain("DO-NOT-STORE-FIRMWARE", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("very-large-payload", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("7.0.1", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("firmwareStatus", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("upToDate", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public void MapDevices_JsonRpcContentTextWrapper_UnwrapsToDeviceList()
    {
        var wrapped = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = "1",
            result = new { content = new[] { new { type = "text", text = CompactListFixture } } },
        });

        var devices = UnifiDeviceMapper.MapDevices(wrapped);
        Assert.Equal(3, devices.Count);
        Assert.Equal("dev-ap-1", devices[0].ExternalId);
        Assert.Equal("Office AP", devices[0].Name);
        Assert.Equal("host-1", devices[0].OrganizationExternalId);
    }

    [Fact]
    public void MapDevices_ToolError_Throws()
    {
        var body = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = "1",
            error = new { code = -32000, message = "unifi auth expired" },
        });

        var ex = Assert.Throws<InvalidOperationException>(() => { UnifiDeviceMapper.MapDevices(body); });
        Assert.Contains("unifi auth expired", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildArgumentsJson_OmitsNextTokenAndHostIdsOnFirstPage()
    {
        var args = UnifiDeviceMapper.BuildArgumentsJson(nextToken: null);
        Assert.Contains("\"pageSize\":50", args, StringComparison.Ordinal);
        Assert.DoesNotContain("nextToken", args, StringComparison.Ordinal);
        Assert.DoesNotContain("hostIds", args, StringComparison.Ordinal);
    }

    [Fact]
    public void MapDevices_ReadsNextTokenFromWrapper()
    {
        const string json = """
            {"data":[{"hostId":"host-1","hostName":"Adroc Capital: 1425 RXR Plaza","devices":[{"id":"dev-ap-1","mac":"aa:bb:cc:dd:ee:01","name":"Office AP"}]}],"nextToken":"tok-2","httpStatusCode":200}
            """;
        UnifiDeviceMapper.MapDevices(json, out var nextToken);
        Assert.Equal("tok-2", nextToken);
        var next = UnifiDeviceMapper.BuildArgumentsJson(nextToken: nextToken);
        Assert.Contains("\"nextToken\":\"tok-2\"", next, StringComparison.Ordinal);
        Assert.Contains("\"pageSize\":50", next, StringComparison.Ordinal);
        Assert.DoesNotContain("hostIds", next, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildArgumentsJson_ClampsPageSizeToMax200()
    {
        var args = UnifiDeviceMapper.BuildArgumentsJson(nextToken: null, pageSize: 20000);
        Assert.Contains("\"pageSize\":200", args, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildArgumentsJson_IncludesHostIdsOnlyWhenProvided()
    {
        var args = UnifiDeviceMapper.BuildArgumentsJson(nextToken: null, hostIds: "host-1,host-2");
        Assert.Contains("\"hostIds\":\"host-1,host-2\"", args, StringComparison.Ordinal);
        Assert.Contains("\"pageSize\":50", args, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PullAsync_SecondCall_Receives_NextToken_From_First_Page()
    {
        var mcp = new RecordingUnifiDeviceMcp { DevicesJson = CompactListFixture };
        var serverId = Guid.NewGuid();

        var devices = await UnifiDeviceMapper.PullAsync(mcp, serverId, pageSize: 1);

        Assert.Equal(3, devices.Count);
        Assert.Equal(2, mcp.Calls.Count);
        Assert.All(mcp.Calls, c => Assert.Equal("unifi_sm_list_devices", c.Tool));
        Assert.DoesNotContain(mcp.Calls, c => c.Tool == "unifi_net_list_devices");
        Assert.Equal(serverId, mcp.Calls[0].ServerId);
        Assert.DoesNotContain("nextToken", mcp.Calls[0].Args, StringComparison.Ordinal);
        Assert.DoesNotContain("hostIds", mcp.Calls[0].Args, StringComparison.Ordinal);
        Assert.Contains("\"pageSize\":1", mcp.Calls[0].Args, StringComparison.Ordinal);
        Assert.Contains("\"nextToken\":\"host-2\"", mcp.Calls[1].Args, StringComparison.Ordinal);
        Assert.Contains("\"pageSize\":1", mcp.Calls[1].Args, StringComparison.Ordinal);
        Assert.DoesNotContain("hostIds", mcp.Calls[1].Args, StringComparison.Ordinal);
    }

    /// <summary>
    /// A full page of host groups that map to no devices must not end the pull. Paging is decided
    /// on the groups the vendor returned, not the devices that mapped — otherwise a single host
    /// without hostId silently abandons every device after it, while the run still reports Succeeded.
    /// </summary>
    [Fact]
    public async Task PullAsync_KeepsPaging_When_A_Full_Page_Contains_A_Dropped_Host()
    {
        const string json = """
            {"data":[
              {"hostName":"No Host Id","devices":[{"id":"dev-drop","mac":"aa:bb:cc:dd:ee:10","name":"Dropped"}]},
              {"hostId":"host-ok","hostName":"Adroc","devices":[{"id":"dev-keep","mac":"aa:bb:cc:dd:ee:11","name":"Kept AP"}]}
            ]}
            """;

        var mcp = new RecordingUnifiDeviceMcp { DevicesJson = json };
        var pulled = await UnifiDeviceMapper.PullAsync(mcp, Guid.NewGuid(), pageSize: 1);

        Assert.Single(pulled);
        Assert.Equal("dev-keep", pulled[0].ExternalId);
        Assert.Equal(2, mcp.Calls.Count);
        Assert.DoesNotContain(pulled, d => d.ExternalId == "dev-drop");
    }

    [Fact]
    public async Task PullAsync_DefaultPull_DoesNotSendHostIds()
    {
        var mcp = new RecordingUnifiDeviceMcp { DevicesJson = CompactListFixture };
        await UnifiDeviceMapper.PullAsync(mcp, Guid.NewGuid());

        Assert.All(mcp.Calls, c =>
        {
            Assert.Equal("unifi_sm_list_devices", c.Tool);
            Assert.DoesNotContain("hostIds", c.Args, StringComparison.Ordinal);
        });
    }

    /// <summary>Serves unifi_sm_list_devices off a fixture wrapper, honouring nextToken/pageSize.</summary>
    private sealed class RecordingUnifiDeviceMcp : IMcpClient
    {
        public List<(Guid ServerId, string Tool, string? Args)> Calls { get; } = [];
        public string DevicesJson { get; init; } = """{"data":[]}""";

        public Task<string> ListToolsAsync(Guid mcpServerId, CancellationToken cancellationToken = default)
            => Task.FromResult("""{"result":{"tools":[]}}""");

        public Task<string> CallToolAsync(Guid mcpServerId, string toolName, string? argumentsJson, CancellationToken cancellationToken = default)
        {
            Calls.Add((mcpServerId, toolName, argumentsJson));
            string? nextToken = null;
            var pageSize = UnifiDeviceMapper.DefaultPageSize;
            if (!string.IsNullOrWhiteSpace(argumentsJson))
            {
                using var doc = JsonDocument.Parse(argumentsJson);
                if (doc.RootElement.TryGetProperty("nextToken", out var t) && t.ValueKind == JsonValueKind.String)
                    nextToken = t.GetString();
                if (doc.RootElement.TryGetProperty("pageSize", out var s) && s.ValueKind == JsonValueKind.Number)
                    pageSize = s.GetInt32();
            }

            var inner = SliceHostsJson(DevicesJson, nextToken, pageSize);
            var body = JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = "1",
                result = new { content = new[] { new { type = "text", text = inner } } },
            });
            return Task.FromResult(body);
        }

        private static string SliceHostsJson(string json, string? nextToken, int pageSize)
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                return """{"data":[]}""";

            var all = data.EnumerateArray().ToList();
            var start = 0;
            if (nextToken is not null)
            {
                start = all.FindIndex(h =>
                    h.ValueKind == JsonValueKind.Object
                    && h.TryGetProperty("hostId", out var id)
                    && id.GetString() == nextToken);
                if (start < 0)
                    start = all.Count;
            }

            var page = all.Skip(start).Take(pageSize).ToList();
            var dataJson = "[" + string.Join(",", page.Select(p => p.GetRawText())) + "]";
            if (start + page.Count < all.Count)
            {
                var outgoing = ReadHostCursor(all[start + page.Count], start + page.Count);
                return $$"""{"data":{{dataJson}},"nextToken":"{{outgoing}}","httpStatusCode":200}""";
            }

            return $$"""{"data":{{dataJson}},"httpStatusCode":200}""";
        }

        private static string ReadHostCursor(JsonElement host, int index)
        {
            if (host.ValueKind == JsonValueKind.Object
                && host.TryGetProperty("hostId", out var id)
                && id.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(id.GetString()))
                return id.GetString()!;
            return $"idx-{index}";
        }
    }
}
