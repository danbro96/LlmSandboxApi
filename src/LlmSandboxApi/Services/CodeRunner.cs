using System.Diagnostics;
using System.Text;
using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.Extensions.Options;

namespace LlmSandboxApi.Services;

public sealed record RunResult(
    string Stdout,
    string Stderr,
    long ExitCode,
    bool TimedOut,
    bool OomKilled,
    long DurationMs,
    bool OutputTruncated);

/// <summary>
/// Runs untrusted code in a throwaway, hardened container on a co-located Docker engine: no network,
/// read-only rootfs (+ small tmpfs), all caps dropped, no-new-privileges, non-root, with memory/cpu/pids
/// caps and a wall-clock kill. The container is removed after the run. In prod the engine uses the gVisor
/// (<c>runsc</c>) runtime on a dedicated low-trust host reached via a scoped socket-proxy.
/// </summary>
public sealed class CodeRunner : IDisposable
{
    private static readonly ActivitySource Activity = new("LlmSandboxApi.Sandbox");

    private readonly SandboxOptions _opts;
    private readonly DockerClient _docker;
    private readonly SemaphoreSlim _slots;

    public CodeRunner(IOptions<SandboxOptions> opts)
    {
        _opts = opts.Value;
        var config = string.IsNullOrWhiteSpace(_opts.DockerHost)
            ? new DockerClientConfiguration()
            : new DockerClientConfiguration(new Uri(_opts.DockerHost));
        _docker = config.CreateClient();
        _slots = new SemaphoreSlim(Math.Max(1, _opts.MaxConcurrent));
    }

    /// <summary>Pure: the image, entrypoint command, and env that run <paramref name="code"/> for a language.</summary>
    internal (string Image, IList<string> Cmd, IList<string> Env) BuildSpec(string language, string code)
    {
        var lang = language.ToLowerInvariant();
        if (!_opts.Images.TryGetValue(lang, out var image))
            throw new ArgumentException($"unsupported language '{language}' (supported: {string.Join(", ", _opts.Images.Keys)})");

        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(code));
        IList<string> cmd = lang switch
        {
            "python" => ["python", "-c", "import os,base64;exec(compile(base64.b64decode(os.environ['SANDBOX_CODE']),'<sandbox>','exec'))"],
            "javascript" => ["node", "-e", "eval(Buffer.from(process.env.SANDBOX_CODE,'base64').toString())"],
            _ => throw new ArgumentException($"unsupported language '{language}'"),
        };
        return (image, cmd, [$"SANDBOX_CODE={encoded}"]);
    }

    public async Task<RunResult> RunAsync(string language, string code, int? timeoutSeconds, CancellationToken ct)
    {
        if (Encoding.UTF8.GetByteCount(code) > _opts.MaxCodeBytes)
            throw new ArgumentException($"code exceeds the {_opts.MaxCodeBytes}-byte limit");

        var (image, cmd, env) = BuildSpec(language, code);
        var timeout = TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds ?? _opts.DefaultTimeoutSeconds, 1, _opts.MaxTimeoutSeconds));

        await _slots.WaitAsync(ct);
        using var activity = Activity.StartActivity("run_code");
        activity?.SetTag("sandbox.language", language.ToLowerInvariant());
        try
        {
            await EnsureImageAsync(image, ct);
            return await ExecAsync(image, cmd, env, timeout, activity, ct);
        }
        finally
        {
            _slots.Release();
        }
    }

    private async Task<RunResult> ExecAsync(
        string image, IList<string> cmd, IList<string> env, TimeSpan timeout, Activity? activity, CancellationToken ct)
    {
        var hostConfig = new HostConfig
        {
            NetworkMode = "none",
            ReadonlyRootfs = true,
            Memory = _opts.MemoryBytes,
            NanoCPUs = (long) (_opts.Cpus * 1_000_000_000),
            PidsLimit = _opts.PidsLimit,
            CapDrop = ["ALL"],
            SecurityOpt = ["no-new-privileges:true"],
            Tmpfs = new Dictionary<string, string> { ["/tmp"] = $"rw,size={_opts.TmpfsBytes},nosuid,nodev" },
        };
        if (!string.IsNullOrWhiteSpace(_opts.Runtime)) hostConfig.Runtime = _opts.Runtime;

        var created = await _docker.Containers.CreateContainerAsync(
            new CreateContainerParameters
            {
                Image = image,
                Cmd = cmd,
                Env = env,
                User = _opts.User,
                WorkingDir = "/tmp",
                NetworkDisabled = true,
                AttachStdout = true,
                AttachStderr = true,
                HostConfig = hostConfig,
            },
            ct);

        var id = created.ID;
        var sw = Stopwatch.StartNew();
        try
        {
            await _docker.Containers.StartContainerAsync(id, new ContainerStartParameters(), ct);

            var timedOut = false;
            using (var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                timeoutCts.CancelAfter(timeout);
                try
                {
                    await _docker.Containers.WaitContainerAsync(id, timeoutCts.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    timedOut = true;
                    try
                    {
                        await _docker.Containers.KillContainerAsync(id, new ContainerKillParameters(), ct);
                    }
                    catch (DockerApiException)
                    {
                        // Already gone — nothing to kill.
                    }
                }
            }

            sw.Stop();
            var (stdout, outTrunc) = Cap(await ReadStdoutAsync(id, ct));
            var (stderr, errTrunc) = Cap(await ReadStderrAsync(id, ct));
            var inspect = await _docker.Containers.InspectContainerAsync(id, ct);

            activity?.SetTag("sandbox.exit_code", inspect.State.ExitCode);
            activity?.SetTag("sandbox.timed_out", timedOut);
            activity?.SetTag("sandbox.oom_killed", inspect.State.OOMKilled);
            return new RunResult(stdout, stderr, inspect.State.ExitCode, timedOut, inspect.State.OOMKilled, sw.ElapsedMilliseconds, outTrunc || errTrunc);
        }
        finally
        {
            try
            {
                await _docker.Containers.RemoveContainerAsync(id, new ContainerRemoveParameters { Force = true }, CancellationToken.None);
            }
            catch (DockerApiException)
            {
                // Best-effort cleanup.
            }
        }
    }

    private async Task<string> ReadStdoutAsync(string id, CancellationToken ct)
    {
        using var stream = await _docker.Containers.GetContainerLogsAsync(
            id, tty: false, new ContainerLogsParameters { ShowStdout = true, ShowStderr = false }, ct);
        var (stdout, _) = await stream.ReadOutputToEndAsync(ct);
        return stdout;
    }

    private async Task<string> ReadStderrAsync(string id, CancellationToken ct)
    {
        using var stream = await _docker.Containers.GetContainerLogsAsync(
            id, tty: false, new ContainerLogsParameters { ShowStdout = false, ShowStderr = true }, ct);
        var (_, stderr) = await stream.ReadOutputToEndAsync(ct);
        return stderr;
    }

    private (string Text, bool Truncated) Cap(string text)
    {
        if (Encoding.UTF8.GetByteCount(text) <= _opts.MaxOutputBytes) return (text, false);
        var bytes = Encoding.UTF8.GetBytes(text);
        return (Encoding.UTF8.GetString(bytes, 0, _opts.MaxOutputBytes), true);
    }

    private async Task EnsureImageAsync(string image, CancellationToken ct)
    {
        try
        {
            await _docker.Images.InspectImageAsync(image, ct);
            return;
        }
        catch (DockerImageNotFoundException)
        {
            // Pull below.
        }

        var (repo, tag) = SplitImage(image);
        await _docker.Images.CreateImageAsync(
            new ImagesCreateParameters { FromImage = repo, Tag = tag }, null, new Progress<JSONMessage>(), ct);
    }

    private static (string Repo, string Tag) SplitImage(string image)
    {
        var slash = image.LastIndexOf('/');
        var colon = image.LastIndexOf(':');
        return colon > slash ? (image[..colon], image[(colon + 1)..]) : (image, "latest");
    }

    public void Dispose() => _docker.Dispose();
}
