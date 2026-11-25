# Health checks in ASP.NET Core (.NET 10)

A small orders service that uses the built-in health checks
(`Microsoft.Extensions.Diagnostics.HealthChecks` and the ASP.NET Core health checks middleware):

| Endpoint | Checks it runs | Answer |
|---|---|---|
| `/health/live` | none | `200 Healthy` while the process can serve HTTP |
| `/health/startup` | `startup` (tag `startup`) | `503 Unhealthy` until the warm-up has finished, then `200 Healthy` |
| `/health/ready` | `startup`, `database`, `payments-api` (tag `ready`) | `200 Healthy`, `200 Degraded` or `503 Unhealthy`, plain text |
| `/health/details` | all | JSON with one entry per check, **only on the management port** (8081, bound to `localhost`) |

| Check | What it does | On failure |
|---|---|---|
| `startup` | fails until the warm-up work has finished | Unhealthy |
| `database` | `AddDbContextCheck<OrdersDb>`: EF Core `Database.CanConnectAsync` on SQLite, 800 ms timeout set with `PostConfigure` | Unhealthy |
| `payments-api` | `GET {Payments:BaseUrl}status` (a missing trailing slash is added) with a 800 ms registration timeout, result kept for 10 s | **Degraded** |

## Things the code shows on purpose

- **The public port listens on every interface; the management port only on `localhost`.** The kubelet
  sends its probes to the Pod IP, so a public port bound to `localhost` would never answer them. The
  `Kestrel:Endpoints` section of `appsettings.json` replaces `Urls`, `ASPNETCORE_URLS` and
  `ASPNETCORE_HTTP_PORTS`; to change a port, set `Kestrel__Endpoints__Public__Url` or
  `Kestrel__Endpoints__Management__Url` (`results/bindings.txt`; `results/live.txt`, P17).
  To move the management port, change `Kestrel__Endpoints__Management__Url` and `Management__Port`
  together: the first one decides where Kestrel listens, the second one which local port
  `/health/details` answers on. With only the URL changed, `/health/details` answers 404.
- **The startup probe has its own route.** `/health/startup` runs only the `startup` check, so the
  probe waits for the warm-up (P1). If the warm-up hangs, the startup probe keeps failing and Kubernetes
  restarts the container.
- **Liveness runs no checks.** If it checked the database, a database outage would make the
  orchestrator restart every instance, and a restart does not fix the database.
- **A shared dependency reports Degraded, not Unhealthy.** With the default status codes, Degraded
  answers 200, so the instance stays in the load balancer. If every instance turned not ready when the
  payments API failed, the whole service would stop taking traffic.
- **The check timeout must be shorter than the probe timeout.** Kubernetes waits 1 s by default
  (`timeoutSeconds`). With a 2 s check timeout and a hanging dependency, the readiness probe fails
  and the Pod leaves the Service (`results/live.txt`, P15). With 800 ms it answers `200 Degraded` (P5).
- **Every probe runs the checks again.** 200 concurrent requests to `/health/ready` made 200 calls to
  the payments API without the cache (P11) and 1 call with it (P13). The price: for up to 10 s the
  answer can be stale (P14). The cache only helps while payments answers: a call cancelled by the
  timeout is not kept, so while payments hangs every request calls it again (10 requests, 10 calls, P16).
- **The detailed report is filtered by the local port of the connection** (`UseHealthChecks(path, port)`),
  not by the Host header. `RequireHost("*:8081")` reads the Host header, so a client on the public port
  can send `Host: localhost:8081` and get the report (`results/contracts.txt`, K12).
- **Exception messages never reach the JSON.** The EF Core check puts the database error text in the
  description (K8); the writer replaces it with the exception type.
- A registration timeout only works if the check honours the cancellation token. A check that blocks
  ignores it (K2).

## Run it

Requires the .NET 10 SDK (tested with SDK 10.0.401, ASP.NET Core runtime 10.0.12). No Docker needed.

```bash
dotnet build HealthChecksDemo.slnx -c Release
dotnet test --solution HealthChecksDemo.slnx -c Release --no-build

# The app listens on port 8080 on every interface (public) and on localhost:8081 (management).
# Without a payments API on localhost:5399, /health/ready answers 200 Degraded.
dotnet run --project src/HealthChecksDemo -c Release

# In-process contract checks (K1-K12) and the runs against the real app (P1-P17).
# ProbeLab starts its own fake payments API on localhost:5399 and the app on ports 5390 and 5391.
dotnet tools/ProbeLab/bin/Release/net10.0/ProbeLab.dll contracts
dotnet tools/ProbeLab/bin/Release/net10.0/ProbeLab.dll live src/HealthChecksDemo/bin/Release/net10.0/HealthChecksDemo.dll
```

`tools/ProbeLab/KubeletSim.cs` applies the documented rules of a Kubernetes `httpGet` probe
(timeout, success when `200 <= status < 400`, failure threshold). It is a simulation, not Kubernetes.

## Layout

| Folder | Content |
|---|---|
| `src/HealthChecksDemo` | The app. `Program.cs` has the registrations and the three endpoints. |
| `tests/HealthChecksDemo.Tests` | xUnit v3: endpoints through `WebApplicationFactory`, the JSON writer and the cache. |
| `tools/ProbeLab` | Contract checks, the fake payments API and the kubelet simulation. |
| `results/` | Raw output: `contracts.txt`, `live.txt`, `bindings.txt`, `tests.txt`. |

## Kubernetes probes for this app

```yaml
startupProbe:
  httpGet: { path: /health/startup, port: 8080 }
  periodSeconds: 5
  failureThreshold: 30
livenessProbe:
  httpGet: { path: /health/live, port: 8080 }
  periodSeconds: 10
  timeoutSeconds: 1
  failureThreshold: 3
readinessProbe:
  httpGet: { path: /health/ready, port: 8080 }
  periodSeconds: 5
  timeoutSeconds: 1
  failureThreshold: 3
```

Not tested on a cluster: the values follow the Kubernetes documentation and `results/live.txt`.

## What this sample does not cover

- Authentication on the management port. The port filter is not authentication. Bound to `localhost`,
  only containers of the same Pod reach it. If you bind it to every interface to read it from another
  Pod, publish it in no Service or Ingress and close it with a NetworkPolicy.
- Authentication on the public health endpoints. `/health/live`, `/health/startup` and `/health/ready` answer anyone who reaches
  the public port; they only say Healthy, Degraded or Unhealthy, but each request to `/health/ready`
  runs the checks.
- Graceful shutdown: the app does not turn not ready on SIGTERM. Kubernetes marks the endpoint as not
  ready when the Pod is deleted.
- Metrics, logs and traces. Health checks say whether an instance should get traffic, not why it is slow.
- A database server. SQLite reads a local file; this sample does not measure a database check that
  goes over the network.

## License

MIT. See [LICENSE](LICENSE).
