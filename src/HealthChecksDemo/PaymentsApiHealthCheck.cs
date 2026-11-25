using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace HealthChecksDemo;

public sealed class PaymentsOptions
{
    public string BaseUrl { get; set; } = "http://localhost:5399/";

    // 0 turns the cache off: every probe calls the payments API.
    public int CacheSeconds { get; set; } = 10;

    // Registration timeout of the check. Keep it below the probe timeout of the orchestrator.
    public int CheckTimeoutMs { get; set; } = 800;
}

// Calls GET {BaseUrl}status. 2xx -> Healthy; any other status or a connection error -> the registration's FailureStatus.
public sealed class PaymentsApiHealthCheck(IHttpClientFactory clients, PaymentsHealthCache cache) : IHealthCheck
{
    public const string ClientName = "payments";

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        cache.GetOrRunAsync(ct => ProbeAsync(context.Registration.FailureStatus, ct), cancellationToken);

    private async Task<HealthCheckResult> ProbeAsync(HealthStatus failureStatus, CancellationToken cancellationToken)
    {
        HttpClient client = clients.CreateClient(ClientName);
        try
        {
            using HttpResponseMessage response = await client.GetAsync(
                "status", HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            int code = (int)response.StatusCode;
            return response.IsSuccessStatusCode
                ? HealthCheckResult.Healthy($"payments-api answered {code}")
                : new HealthCheckResult(failureStatus, $"payments-api answered {code}");
        }
        catch (HttpRequestException exception)
        {
            return new HealthCheckResult(failureStatus, "payments-api unreachable", exception);
        }
    }
}

// Keeps the last payments-api result for CacheSeconds, so a burst of probes costs one call to the
// dependency. Only one call runs at a time. A call cancelled by the timeout is not cached.
public sealed class PaymentsHealthCache(PaymentsOptions options, TimeProvider time)
{
    private sealed record Entry(HealthCheckResult Result, long Timestamp);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile Entry? _last;

    public async Task<HealthCheckResult> GetOrRunAsync(
        Func<CancellationToken, Task<HealthCheckResult>> probe,
        CancellationToken cancellationToken)
    {
        TimeSpan ttl = TimeSpan.FromSeconds(options.CacheSeconds);
        if (ttl <= TimeSpan.Zero)
        {
            return await probe(cancellationToken);
        }

        if (TryGetFresh(ttl, out HealthCheckResult cached))
        {
            return cached;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            // Another probe may have refreshed the entry while this one waited.
            if (TryGetFresh(ttl, out cached))
            {
                return cached;
            }

            HealthCheckResult result = await probe(cancellationToken);
            _last = new Entry(result, time.GetTimestamp());
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    private bool TryGetFresh(TimeSpan ttl, out HealthCheckResult result)
    {
        Entry? last = _last;
        if (last is not null && time.GetElapsedTime(last.Timestamp) < ttl)
        {
            result = last.Result;
            return true;
        }

        result = default;
        return false;
    }
}
