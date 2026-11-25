using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace HealthChecksDemo.Tests;

public sealed class UnitTests
{
    private sealed class ManualTime : TimeProvider
    {
        public long Ticks { get; set; }

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Ticks;
    }

    [Fact]
    public async Task Json_report_shows_the_exception_type_but_never_its_message()
    {
        var entries = new Dictionary<string, HealthReportEntry>
        {
            ["database"] = new(HealthStatus.Unhealthy, "SQLite Error 1: 'no such table: Orders'.", TimeSpan.Zero,
                new InvalidOperationException("SQLite Error 1: 'no such table: Orders'."), null),
            ["payments-api"] = new(HealthStatus.Degraded, "A timeout occurred while running check.", TimeSpan.Zero,
                new TaskCanceledException(), null),
            ["startup"] = new(HealthStatus.Healthy, "warm-up finished", TimeSpan.Zero, null, null),
        };
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();

        await HealthReportJson.WriteAsync(context, new HealthReport(entries, TimeSpan.Zero));

        context.Response.Body.Position = 0;
        string body = await new StreamReader(context.Response.Body).ReadToEndAsync(TestContext.Current.CancellationToken);
        Assert.DoesNotContain("no such table", body);
        using JsonDocument json = JsonDocument.Parse(body);
        string?[] descriptions = json.RootElement.GetProperty("checks").EnumerateArray()
            .Select(c => c.GetProperty("description").GetString()).ToArray();
        Assert.Equal(["failed with InvalidOperationException", "timed out or canceled", "warm-up finished"], descriptions);
        Assert.Equal("Unhealthy", json.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Cache_runs_the_probe_once_inside_the_window_and_again_after_it()
    {
        var time = new ManualTime();
        var cache = new PaymentsHealthCache(new PaymentsOptions { CacheSeconds = 10 }, time);
        int calls = 0;
        Task<HealthCheckResult> Probe(CancellationToken _) { calls++; return Task.FromResult(HealthCheckResult.Healthy()); }

        await cache.GetOrRunAsync(Probe, TestContext.Current.CancellationToken);
        time.Ticks = TimeSpan.FromSeconds(9.9).Ticks;
        await cache.GetOrRunAsync(Probe, TestContext.Current.CancellationToken);
        Assert.Equal(1, calls);

        time.Ticks = TimeSpan.FromSeconds(10).Ticks;
        await cache.GetOrRunAsync(Probe, TestContext.Current.CancellationToken);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Cache_with_zero_seconds_runs_the_probe_every_time()
    {
        var cache = new PaymentsHealthCache(new PaymentsOptions { CacheSeconds = 0 }, new ManualTime());
        int calls = 0;
        Task<HealthCheckResult> Probe(CancellationToken _) { calls++; return Task.FromResult(HealthCheckResult.Healthy()); }

        for (int i = 0; i < 3; i++)
        {
            await cache.GetOrRunAsync(Probe, TestContext.Current.CancellationToken);
        }

        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task Cache_does_not_keep_a_cancelled_probe()
    {
        var cache = new PaymentsHealthCache(new PaymentsOptions { CacheSeconds = 10 }, new ManualTime());
        int calls = 0;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cache.GetOrRunAsync(
            _ => { calls++; throw new OperationCanceledException(); },
            TestContext.Current.CancellationToken));
        await cache.GetOrRunAsync(
            _ => { calls++; return Task.FromResult(HealthCheckResult.Healthy()); },
            TestContext.Current.CancellationToken);

        Assert.Equal(2, calls);
    }
}
