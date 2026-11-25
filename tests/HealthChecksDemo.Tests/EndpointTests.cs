using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace HealthChecksDemo.Tests;

public sealed class EndpointTests
{
    // Each test gets its own database file and settings. "payments.invalid" never resolves (RFC 2606),
    // so the payments check fails fast instead of waiting for a connection timeout.
    private static WebApplicationFactory<Program> CreateFactory(int warmUpSeconds, string paymentsBaseUrl = "http://payments.invalid/") =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Orders:DatabasePath", Path.Combine(Path.GetTempPath(), $"hc-test-{Guid.NewGuid():N}.db"));
            builder.UseSetting("Startup:WarmUpSeconds", warmUpSeconds.ToString());
            builder.UseSetting("Payments:BaseUrl", paymentsBaseUrl);
            builder.UseSetting("Payments:CacheSeconds", "0");
        });

    [Fact]
    public async Task Live_is_200_Healthy_while_the_warm_up_still_runs()
    {
        await using WebApplicationFactory<Program> factory = CreateFactory(warmUpSeconds: 600);
        HttpClient client = factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync("/health/live", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Ready_is_503_Unhealthy_while_the_warm_up_still_runs()
    {
        await using WebApplicationFactory<Program> factory = CreateFactory(warmUpSeconds: 600);
        HttpClient client = factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync("/health/ready", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("Unhealthy", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Ready_is_200_Degraded_when_only_payments_fails()
    {
        await using WebApplicationFactory<Program> factory = CreateFactory(warmUpSeconds: 0);
        HttpClient client = factory.CreateClient();
        await WaitUntilWarmAsync(factory);

        HttpResponseMessage response = await client.GetAsync("/health/ready", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Degraded", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Details_is_404_on_a_connection_that_is_not_on_the_management_port()
    {
        // TestServer connections report local port 0, never the management port.
        await using WebApplicationFactory<Program> factory = CreateFactory(warmUpSeconds: 0);
        HttpClient client = factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync("/health/details", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Readiness_runs_three_checks_and_liveness_runs_none()
    {
        await using WebApplicationFactory<Program> factory = CreateFactory(warmUpSeconds: 0);
        HealthCheckServiceOptions options = factory.Services.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value;

        string[] ready = options.Registrations.Where(r => r.Tags.Contains("ready")).Select(r => r.Name).ToArray();

        Assert.Equal(["startup", "database", "payments-api"], ready);
        Assert.Equal(HealthStatus.Degraded, options.Registrations.Single(r => r.Name == "payments-api").FailureStatus);
        Assert.Equal(TimeSpan.FromMilliseconds(800), options.Registrations.Single(r => r.Name == "payments-api").Timeout);
    }

    [Fact]
    public async Task Startup_is_503_during_the_warm_up_and_200_after_it()
    {
        await using WebApplicationFactory<Program> during = CreateFactory(warmUpSeconds: 600);
        HttpResponseMessage before = await during.CreateClient().GetAsync("/health/startup", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, before.StatusCode);

        await using WebApplicationFactory<Program> after = CreateFactory(warmUpSeconds: 0);
        HttpClient client = after.CreateClient();
        await WaitUntilWarmAsync(after);
        HttpResponseMessage response = await client.GetAsync("/health/startup", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Database_check_has_an_800_ms_timeout()
    {
        await using WebApplicationFactory<Program> factory = CreateFactory(warmUpSeconds: 0);
        HealthCheckServiceOptions options = factory.Services.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value;

        Assert.Equal(TimeSpan.FromMilliseconds(800), options.Registrations.Single(r => r.Name == "database").Timeout);
    }

    [Theory]
    [InlineData("http://payments.invalid/v1", "http://payments.invalid/v1/status")]
    [InlineData("http://payments.invalid/v1/", "http://payments.invalid/v1/status")]
    public async Task Payments_base_url_keeps_its_last_segment(string baseUrl, string expected)
    {
        await using WebApplicationFactory<Program> factory = CreateFactory(warmUpSeconds: 0, paymentsBaseUrl: baseUrl);
        HttpClient payments = factory.Services.GetRequiredService<IHttpClientFactory>().CreateClient(PaymentsApiHealthCheck.ClientName);

        Assert.Equal(expected, new Uri(payments.BaseAddress!, "status").ToString());
    }

    private static async Task WaitUntilWarmAsync(WebApplicationFactory<Program> factory)
    {
        StartupGate gate = factory.Services.GetRequiredService<StartupGate>();
        for (int i = 0; i < 100 && !gate.IsReady; i++)
        {
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        Assert.True(gate.IsReady);
    }
}
