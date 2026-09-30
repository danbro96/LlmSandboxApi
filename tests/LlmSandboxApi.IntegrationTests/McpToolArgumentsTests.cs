using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace LlmSandboxApi.IntegrationTests;

/// <summary>Every tool refuses an argument its schema doesn't declare, before it runs (so this has no side effects).
/// The rules themselves are tested in LupiraGeoApi, which holds the reference copy of StrictToolArguments.</summary>
public sealed class McpToolArgumentsTests(LlmSandboxApiTestFactory factory) : IClassFixture<LlmSandboxApiTestFactory>, IDisposable
{
    private const string ApiKey = "test-key";

    private readonly WebApplicationFactory<Program> _keyed = factory.WithWebHostBuilder(b => b
        .UseSetting("Auth:ApiKeys:0:Key", ApiKey)
        .UseSetting("Auth:ApiKeys:0:Name", "test"));

    public void Dispose() => _keyed.Dispose();

    private async Task<McpClient> ConnectAsync()
    {
        var http = KeyedClient();
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions { Endpoint = new Uri(http.BaseAddress!, "/mcp"), TransportMode = HttpTransportMode.StreamableHttp },
            http, ownsHttpClient: true);
        return await McpClient.CreateAsync(transport);
    }

    private HttpClient KeyedClient()
    {
        var http = _keyed.CreateClient();
        http.DefaultRequestHeaders.Add("X-API-Key", ApiKey);
        return http;
    }

    [Fact]
    public async Task Every_tool_rejects_an_undeclared_argument()
    {
        await using var mcp = await ConnectAsync();
        var tools = await mcp.ListToolsAsync();
        Assert.NotEmpty(tools);
        foreach (var tool in tools)
        {
            var result = await mcp.CallToolAsync(tool.Name, new Dictionary<string, object?> { ["__undeclared"] = 1 });

            Assert.True(result.IsError, tool.Name);
            var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
            Assert.StartsWith($"Invalid arguments for '{tool.Name}': unknown __undeclared", text);
        }
    }
}
