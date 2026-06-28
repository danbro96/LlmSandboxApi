using Docker.DotNet;
using LlmSandboxApi.Services;
using Microsoft.Extensions.Options;
using Xunit;

namespace LlmSandboxApi.IntegrationTests;

/// <summary>
/// Exercises the real container runner against the local Docker engine. Self-skips when no engine is
/// reachable (e.g. CI without Docker). First run pulls the language image.
/// </summary>
public class CodeRunnerTests
{
    private static async Task<bool> DockerAvailableAsync()
    {
        try
        {
            using var client = new DockerClientConfiguration().CreateClient();
            await client.System.PingAsync();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static CodeRunner Runner() => new(Options.Create(new SandboxOptions()));

    [SkippableFact]
    public async Task Runs_python_and_captures_stdout()
    {
        Skip.IfNot(await DockerAvailableAsync(), "Docker engine not reachable");
        using var runner = Runner();

        var result = await runner.RunAsync("python", "print(2 + 2)", timeoutSeconds: 60, CancellationToken.None);

        Assert.False(result.TimedOut);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("4", result.Stdout.Trim());
    }

    [SkippableFact]
    public async Task Reports_nonzero_exit_and_stderr()
    {
        Skip.IfNot(await DockerAvailableAsync(), "Docker engine not reachable");
        using var runner = Runner();

        var result = await runner.RunAsync("python", "import sys; sys.stderr.write('boom'); sys.exit(3)", timeoutSeconds: 60, CancellationToken.None);

        Assert.Equal(3, result.ExitCode);
        Assert.Contains("boom", result.Stderr);
    }

    [SkippableFact]
    public async Task Times_out_a_long_running_program()
    {
        Skip.IfNot(await DockerAvailableAsync(), "Docker engine not reachable");
        using var runner = Runner();

        var result = await runner.RunAsync("python", "import time; time.sleep(60)", timeoutSeconds: 2, CancellationToken.None);

        Assert.True(result.TimedOut);
    }

    [SkippableFact]
    public async Task Sandbox_has_no_network()
    {
        Skip.IfNot(await DockerAvailableAsync(), "Docker engine not reachable");
        using var runner = Runner();

        // socket() to a public IP must fail fast with no network namespace.
        const string code = "import socket\ntry:\n s=socket.create_connection(('1.1.1.1',53),timeout=3); print('REACHED')\nexcept Exception as e:\n print('BLOCKED')";
        var result = await runner.RunAsync("python", code, timeoutSeconds: 60, CancellationToken.None);

        Assert.Equal("BLOCKED", result.Stdout.Trim());
    }
}
