namespace LlmSandboxApi.Services;

public sealed class SandboxOptions
{
    /// <summary>Docker engine endpoint. Empty = the local default engine (dev). In prod, point at the
    /// scoped socket-proxy, e.g. <c>tcp://socket-proxy:2375</c>.</summary>
    public string? DockerHost { get; set; }

    /// <summary>Container runtime for runs. Empty = Docker default (dev). Set to <c>runsc</c> (gVisor) in prod.</summary>
    public string Runtime { get; set; } = string.Empty;

    /// <summary>UID:GID the code runs as (non-root).</summary>
    public string User { get; set; } = "1000:1000";

    /// <summary>language → container image. The host pre-pulls these; runs have no network.</summary>
    public Dictionary<string, string> Images { get; set; } = new()
    {
        ["python"] = "python:3.12-slim",
        ["javascript"] = "node:22-slim",
    };

    public int DefaultTimeoutSeconds { get; set; } = 10;

    public int MaxTimeoutSeconds { get; set; } = 60;

    public long MemoryBytes { get; set; } = 256 * 1024 * 1024;

    public double Cpus { get; set; } = 1.0;

    public int PidsLimit { get; set; } = 128;

    public long TmpfsBytes { get; set; } = 64 * 1024 * 1024;

    public int MaxCodeBytes { get; set; } = 100_000;

    public int MaxOutputBytes { get; set; } = 64 * 1024;

    public int MaxConcurrent { get; set; } = 4;
}
