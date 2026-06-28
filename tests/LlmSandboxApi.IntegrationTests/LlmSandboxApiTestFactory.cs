using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace LlmSandboxApi.IntegrationTests;

/// <summary>Hosts the real app in-process for boot/auth smoke tests (the Docker engine is not touched at boot).</summary>
public sealed class LlmSandboxApiTestFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.UseEnvironment("Development");
}
