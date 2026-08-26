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
