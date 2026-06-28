using LlmSandboxApi.Auth;
using LlmSandboxApi.Endpoints;
using LlmSandboxApi.Services;
using Microsoft.OpenApi;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Scalar.AspNetCore;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<ApiKeyAuthOptions>(builder.Configuration.GetSection("Auth"));
builder.Services.Configure<SandboxOptions>(builder.Configuration.GetSection("Sandbox"));

// The code runner talks to a co-located Docker engine (the scoped socket-proxy in prod) and owns a
// concurrency gate, so it is a singleton.
builder.Services.AddSingleton<CodeRunner>();

// MCP agent surface. Mounted at /mcp over Streamable HTTP, secured by the X-API-Key scheme (see
// MapMcp below), and kept LAN/WireGuard-only — never published through the Cloudflare Tunnel.
builder.Services
    .AddMcpServer()
    .WithHttpTransport()
    .WithToolsFromAssembly();

builder.Services.AddAppHealthChecks();

builder.Services
    .AddAuthentication(ApiKeyAuthOptions.SchemeName)
    .AddScheme<ApiKeyAuthOptions, ApiKeyAuthenticationHandler>(ApiKeyAuthOptions.SchemeName, opts =>
    {
        var section = builder.Configuration.GetSection("Auth");
        section.Bind(opts);
    });
builder.Services.AddAuthorization();

builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    var permitsPerMinute = builder.Configuration.GetValue("RateLimit:RequestsPerMinute", 60);
    o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
    {
        var key = ctx.User.Identity?.Name ?? ctx.Connection.RemoteIpAddress?.ToString() ?? "anon";
        return RateLimitPartition.GetTokenBucketLimiter(key, _ => new TokenBucketRateLimiterOptions
        {
            TokenLimit = permitsPerMinute,
            TokensPerPeriod = permitsPerMinute,
            ReplenishmentPeriod = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true,
        });
    });
});

var allowedOrigins = builder.Configuration.GetSection("Auth:AllowedOrigins").Get<string[]>() ?? Array.Empty<string>();
if (allowedOrigins.Length > 0)
{
    builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
        p.WithOrigins(allowedOrigins).AllowAnyHeader().AllowAnyMethod()));
}

builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 4 * 1024 * 1024);

builder.Services.AddOpenApi("v1", options =>
{
    options.AddDocumentTransformer((document, context, _) =>
    {
        document.Info = new()
        {
            Title = "LlmSandboxApi",
            Version = "v1",
            Description =
                "Self-hosted code-execution sandbox for LLM agents, exposed over MCP at /mcp (LAN-only). " +
                "Runs untrusted Python/JavaScript in throwaway, network-isolated, resource-capped containers. " +
                "Authenticate with your key in the X-API-Key header.",
        };
        document.Components ??= new();
        document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
        document.Components.SecuritySchemes["ApiKey"] = new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.ApiKey,
            In = ParameterLocation.Header,
            Name = ApiKeyAuthOptions.HeaderName,
            Description = "API key. Send in the X-API-Key header.",
        };
        return Task.CompletedTask;
    });
});

var otlpEndpoint = builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];
if (!string.IsNullOrWhiteSpace(otlpEndpoint))
{
    builder.Services.AddOpenTelemetry()
        .ConfigureResource(r => r.AddService(
            serviceName: "llm-sandbox-api",
            serviceVersion: typeof(Program).Assembly.GetName().Version?.ToString() ?? "0.0.0"))
        .WithTracing(t => t
            .AddSource("LlmSandboxApi.*")
            .AddAspNetCoreInstrumentation(o => o.RecordException = true)
            .AddHttpClientInstrumentation()
            .AddOtlpExporter())
        .WithMetrics(m => m
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            .AddRuntimeInstrumentation()
            .AddOtlpExporter());

    builder.Logging.AddOpenTelemetry(o =>
    {
        o.IncludeFormattedMessage = true;
        o.IncludeScopes = true;
        o.AddOtlpExporter();
    });
}

var app = builder.Build();

if (allowedOrigins.Length > 0) app.UseCors();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

// Keep the MCP surface LAN/WireGuard-only: 404 any /mcp request that arrived via the Cloudflare Tunnel.
app.UseMcpLanOnly();

app.MapOpenApi("/openapi/{documentName}.json").AllowAnonymous();
app.MapScalarApiReference("/scalar", o => o
        .WithTitle("LlmSandboxApi")
        .WithTheme(ScalarTheme.BluePlanet))
    .AllowAnonymous();

app.MapAppHealthChecks(app.Environment);

// Agent MCP surface (Streamable HTTP). Mapped AFTER UseAuthentication/UseAuthorization so the same
// X-API-Key scheme validates it; RequireAuthorization rejects anonymous calls with 401. LAN-only.
app.MapMcp("/mcp").RequireAuthorization();

app.Run();

// Exposed for WebApplicationFactory<Program> in the integration tests.
public partial class Program;
