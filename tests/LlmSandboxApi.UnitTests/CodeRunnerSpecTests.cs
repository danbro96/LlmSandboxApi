using LlmSandboxApi.Services;
using Microsoft.Extensions.Options;
using Xunit;

namespace LlmSandboxApi.UnitTests;

/// <summary>The pure run-spec mapping + input guards (no Docker engine involved).</summary>
public class CodeRunnerSpecTests
{
    private static CodeRunner Runner(SandboxOptions? opts = null) => new(Options.Create(opts ?? new SandboxOptions()));

    [Theory]
    [InlineData("python", "python:3.12-slim", "python")]
    [InlineData("JavaScript", "node:22-slim", "node")]
    public void BuildSpec_maps_language_to_image_and_interpreter(string language, string image, string interpreter)
    {
        var (img, cmd, env) = Runner().BuildSpec(language, "print(1)");
        Assert.Equal(image, img);
        Assert.Equal(interpreter, cmd[0]);
        Assert.Contains(env, e => e.StartsWith("SANDBOX_CODE="));
    }

    [Fact]
    public void BuildSpec_rejects_unsupported_language()
        => Assert.Throws<ArgumentException>(() => Runner().BuildSpec("ruby", "puts 1"));

    [Fact]
    public async Task RunAsync_rejects_oversized_code()
    {
        var runner = Runner(new SandboxOptions { MaxCodeBytes = 10 });
        await Assert.ThrowsAsync<ArgumentException>(
            () => runner.RunAsync("python", new string('x', 100), timeoutSeconds: null, CancellationToken.None));
    }
}
