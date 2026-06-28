using System.Net;
using System.Net.Http.Json;
using Xunit;

namespace LlmSandboxApi.IntegrationTests;

/// <summary>Boot smoke: the app starts in-process, serves health + OpenAPI anonymously, and gates /mcp behind X-API-Key.</summary>
public sealed class SmokeTests(LlmSandboxApiTestFactory factory) : IClassFixture<LlmSandboxApiTestFactory>
{
    [Fact]
    public async Task Livez_is_ok()
    {
        var resp = await factory.CreateClient().GetAsync("/livez");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task OpenApi_document_is_served()
    {
        var resp = await factory.CreateClient().GetAsync("/openapi/v1.json");
        resp.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Mcp_without_an_api_key_is_unauthorized()
    {
        var resp = await factory.CreateClient().PostAsJsonAsync("/mcp", new { });
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }
}
