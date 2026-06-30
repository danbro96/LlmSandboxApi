# LlmSandboxApi

A stateless .NET 10 **MCP code-execution sandbox** for LLM agents. It runs short, untrusted
Python/JavaScript in throwaway, network-isolated, resource-capped Docker containers and returns their
output over MCP. Because it executes untrusted code, it is built to run on a **dedicated low-trust host**
behind a gVisor (`runsc`) boundary, reachable only from trusted agent hosts.

## What it does

- **`run_code`** (the one tool) — runs a `python` or `javascript` snippet in a fresh container and returns
  `stdout`, `stderr`, `exitCode`, `timedOut`, `oomKilled`, `durationMs`, `outputTruncated`.

Each run is isolated by construction: `--runtime=runsc` (gVisor user-space kernel), `--network none`,
read-only rootfs + a small `/tmp` tmpfs, all Linux capabilities dropped, `no-new-privileges`, a non-root
UID, and memory/CPU/pids/wall-clock caps — then the container is removed. Code is injected base64 via an
env var (no shell/arg injection). The app talks to the Docker engine only through a scoped
`docker-socket-proxy`; it never mounts the raw socket.

## Surface

Agent **MCP** at `/mcp` (Streamable HTTP) plus `/openapi/v1.json`, `/scalar`, `/livez`, `/readyz`. The MCP
surface is **LAN/WireGuard-only** — not meant to be tunnelled publicly. Health probes are process-up only
(stateless).

## Auth & exposure

`X-API-Key` header (or `?api_key=` for the MCP transport), constant-time matched against `Auth:ApiKeys[]`.
Run it on an isolated host with the API port firewalled to your agent host(s) only.

## Config

| Section | Key | Default | Notes |
|---|---|---|---|
| `Auth` | `ApiKeys[]` (`Key`, `Name`) | — | accepted `X-API-Key`s (≥1 required) |
| `Auth` | `AllowedOrigins[]` | empty | CORS off unless set |
| `RateLimit` | `RequestsPerMinute` | `60` | per-key token bucket; 429 over limit |
| `Sandbox` | `DockerHost` | local engine | prod: `tcp://socket-proxy:2375` |
| `Sandbox` | `Runtime` | empty | **set `runsc`** for untrusted code |
| `Sandbox` | `Images` (`python`,`javascript`) | `python:3.12-slim`, `node:22-slim` | language images (pre-pull; runs have no network) |
| `Sandbox` | `MemoryBytes` / `Cpus` / `PidsLimit` | 256 MiB / 1.0 / 128 | per-run caps |
| `Sandbox` | `DefaultTimeoutSeconds` / `MaxTimeoutSeconds` | 10 / 60 | wall-clock kill |
| `Sandbox` | `MaxConcurrent` | 4 | concurrent runs (semaphore) |
| `Sandbox` | `User` | `1000:1000` | non-root run UID |
| OTEL | `OTEL_EXPORTER_OTLP_ENDPOINT`, … | unset → off | OTLP export when set |

Env-var form uses `__` for nesting (e.g. `Sandbox__Runtime`, `Sandbox__MemoryBytes`, `Auth__ApiKeys__0__Key`).
See [deploy/.env.example](deploy/.env.example).

## Build & test

```bash
dotnet test LlmSandboxApi.slnx                       # unit (run-spec mapping) — fast, no I/O
dotnet test tests/LlmSandboxApi.IntegrationTests     # exercises CodeRunner against a real Docker engine
```

## Deploy

Built and pushed by CI to `danbro96/llm-sandbox-api`. Runs as a two-service `docker compose` stack
(`llm-sandbox-api` + a scoped `socket-proxy`) on a **dedicated host with Docker + gVisor (`runsc`)
installed** and the language images pre-pulled. See [deploy/compose.yaml](deploy/compose.yaml) for a
genericized stack; the live host bring-up + runbook live in the private infra docs.
