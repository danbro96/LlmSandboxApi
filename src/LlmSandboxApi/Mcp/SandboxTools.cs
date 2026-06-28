using System.ComponentModel;
using LlmSandboxApi.Services;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace LlmSandboxApi.Mcp;

[McpServerToolType]
public sealed class SandboxTools
{
    private readonly CodeRunner _runner;

    public SandboxTools(CodeRunner runner) => _runner = runner;

    [McpServerTool(Name = "run_code")]
    [Description("Run a short program in a throwaway, network-isolated sandbox and return its output. " +
        "language = 'python' or 'javascript'. The program has no network access, a read-only filesystem " +
        "(except /tmp), and memory/CPU/time limits. Use print/console.log to produce output.")]
    public async Task<RunResult> RunCode(
        [Description("Language: 'python' or 'javascript'.")] string language,
        [Description("The program source code.")] string code,
        [Description("Wall-clock timeout in seconds (default 10, max 60).")] int? timeoutSeconds = null,
        CancellationToken ct = default)
    {
        try
        {
            return await _runner.RunAsync(language, code, timeoutSeconds, ct);
        }
        catch (ArgumentException ex)
        {
            throw new McpException(ex.Message);
        }
        catch (Docker.DotNet.DockerApiException ex)
        {
            throw new McpException($"sandbox engine error: {ex.Message}");
        }
    }
}
