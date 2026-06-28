namespace LlmSandboxApi.Endpoints;

/// <summary>
/// Defence-in-depth backstop keeping the MCP surface LAN/WireGuard-only.
///
/// <para>
/// The PRIMARY control is the Cloudflare Tunnel ingress simply not routing <c>/mcp</c> to the
/// container (host config, outside this repo — see the deploy notes). This middleware is the
/// belt-and-suspenders that survives an ingress mistake: Cloudflare's edge stamps <c>CF-Ray</c>/
/// <c>CF-Connecting-IP</c> on everything that arrives through the tunnel, and a direct
/// LAN/WireGuard request to the container port never carries them — so a tunnelled request to
/// <c>/mcp*</c> is answered with 404 (indistinguishable from "no such route") before it reaches
/// the MCP handler.
/// </para>
///
/// Registered as plain middleware rather than an endpoint filter because <c>MapMcp</c>'s
/// streaming endpoint does not run the minimal-API filter pipeline.
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
