namespace LlmSandboxApi.Endpoints;

/// <summary>
/// Defence-in-depth backstop keeping the prefixes below LAN/WireGuard-only. The PRIMARY control is the
/// Cloudflare Tunnel ingress not routing them to the container (host config, outside this repo). This
/// middleware survives an ingress mistake: anything through the Cloudflare edge carries <c>CF-Ray</c>/
/// <c>CF-Connecting-IP</c>, which a direct LAN/WireGuard request never does — so a tunnelled hit is
/// answered 404, indistinguishable from "no such route". Plain middleware rather than an endpoint filter
/// because <c>MapMcp</c>'s streaming endpoint does not run the minimal-API filter pipeline.
/// </summary>
internal static class McpExposure
{
    private const string PathPrefix = "/mcp";
    private static readonly string[] CloudflareHeaders = ["CF-Ray", "CF-Connecting-IP"];

    public static IApplicationBuilder UseMcpLanOnly(this WebApplication app)
    {
        return app.Use(async (ctx, next) =>
        {
            if (ctx.Request.Path.StartsWithSegments(PathPrefix)
                && CloudflareHeaders.Any(h => ctx.Request.Headers.ContainsKey(h)))
            {
                // Came in through the Cloudflare Tunnel — pretend the route doesn't exist.
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            await next(ctx);
        });
    }
}
