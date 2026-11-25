using System.Diagnostics;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace ProbeLab;

// In-process checks of how HealthCheckService and the EF Core DbContext check behave.
public static class Contracts
{
    public static async Task RunAsync()
    {
        await RunCaseAsync("K1 three checks of 500 ms each", b => b
            .AddAsyncCheck("a", async ct => { await Task.Delay(500, ct); return HealthCheckResult.Healthy(); })
            .AddAsyncCheck("b", async ct => { await Task.Delay(500, ct); return HealthCheckResult.Healthy(); })
            .AddAsyncCheck("c", async ct => { await Task.Delay(500, ct); return HealthCheckResult.Healthy(); }));

        await RunCaseAsync("K2 timeout 300 ms, check blocks 1500 ms and ignores the token", b => b
            .AddCheck("blocking", () => { Thread.Sleep(1500); return HealthCheckResult.Healthy(); }, timeout: TimeSpan.FromMilliseconds(300)));

        await RunCaseAsync("K3 timeout 300 ms, check waits 5000 ms with the token", b => b
            .AddAsyncCheck("waiting", async ct => { await Task.Delay(5000, ct); return HealthCheckResult.Healthy(); }, timeout: TimeSpan.FromMilliseconds(300)));

        await RunCaseAsync("K4 failureStatus Degraded, check throws", b => b
            .AddCheck("throws", new FuncCheck(() => throw new InvalidOperationException("dependency said no")), HealthStatus.Degraded, tags: []));

        await RunCaseAsync("K5 failureStatus Degraded, check returns Unhealthy explicitly", b => b
            .AddCheck("explicit", new FuncCheck(() => HealthCheckResult.Unhealthy("explicit result")), HealthStatus.Degraded, tags: []));

        await RunCaseAsync("K6a Healthy + Degraded", b => b
            .AddCheck("h", () => HealthCheckResult.Healthy())
            .AddCheck("d", () => HealthCheckResult.Degraded()));
        await RunCaseAsync("K6b Degraded + Unhealthy", b => b
            .AddCheck("d", () => HealthCheckResult.Degraded())
            .AddCheck("u", () => HealthCheckResult.Unhealthy()));
        await RunCaseAsync("K6c no checks registered", _ => { });

        string folder = Directory.CreateTempSubdirectory("hc-contracts-").FullName;
        try
        {
            string missing = Path.Combine(folder, "missing.db");
            await RunCaseAsync("K7 DbContext check, failureStatus Degraded, database file missing", b => b
                .AddDbContextCheck<ProbeDb>("database", failureStatus: HealthStatus.Degraded),
                services => services.AddDbContext<ProbeDb>(o => o.UseSqlite(Sqlite(missing))));
            Console.WriteLine($"K7 file created by the check: {File.Exists(missing)}");

            string existing = Path.Combine(folder, "existing.db");
            await using (var connection = new SqliteConnection(Sqlite(existing)))
            {
                await connection.OpenAsync();
                await using SqliteCommand create = connection.CreateCommand();
                create.CommandText = "CREATE TABLE Orders (Id INTEGER PRIMARY KEY)";
                await create.ExecuteNonQueryAsync();
            }

            await RunCaseAsync("K8 DbContext check, failureStatus Degraded, test query throws", b => b
                .AddDbContextCheck<ProbeDb>("database", failureStatus: HealthStatus.Degraded,
                    customTestQuery: async (db, ct) =>
                    {
                        await db.Database.ExecuteSqlRawAsync("SELECT * FROM MissingTable", ct);
                        return true;
                    }),
                services => services.AddDbContext<ProbeDb>(o => o.UseSqlite(Sqlite(existing))));

            await RunCaseAsync("K9 DbContext check, failureStatus Degraded, timeout 300 ms, test query waits 5000 ms", b => b
                .AddDbContextCheck<ProbeDb>("database", failureStatus: HealthStatus.Degraded,
                    customTestQuery: async (db, ct) =>
                    {
                        await Task.Delay(5000, ct);
                        return true;
                    }),
                services =>
                {
                    services.AddDbContext<ProbeDb>(o => o.UseSqlite(Sqlite(existing)));
                    // AddDbContextCheck has no timeout parameter; PostConfigure sets the registration's Timeout.
                    services.PostConfigure<HealthCheckServiceOptions>(o =>
                        o.Registrations.Single(r => r.Name == "database").Timeout = TimeSpan.FromMilliseconds(300));
                });
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(folder, recursive: true);
        }

        try
        {
            await RunCaseAsync("K10 two checks named \"db\" and \"DB\"", b => b
                .AddCheck("db", () => HealthCheckResult.Healthy())
                .AddCheck("DB", () => HealthCheckResult.Healthy()));
        }
        catch (ArgumentException exception)
        {
            Console.WriteLine($"K10 -> {exception.GetType().Name}: {exception.Message}");
        }

        try
        {
            _ = new HealthCheckOptions
            {
                ResultStatusCodes = new Dictionary<HealthStatus, int>
                {
                    [HealthStatus.Healthy] = 200,
                    [HealthStatus.Unhealthy] = 503,
                },
            };
        }
        catch (InvalidOperationException exception)
        {
            Console.WriteLine($"K11 ResultStatusCodes without Degraded -> {exception.GetType().Name}: {exception.Message}");
        }

        await HostSpoofingAsync();
    }

    // K12: RequireHost reads the Host header; UseHealthChecks(path, port) reads the local port of the connection.
    private static async Task HostSpoofingAsync()
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://localhost:5392", "http://localhost:5393");
        builder.Logging.ClearProviders();
        builder.Services.AddHealthChecks().AddCheck("secret", () => HealthCheckResult.Healthy("internal detail"));
        await using WebApplication app = builder.Build();
        app.MapHealthChecks("/by-host").RequireHost("*:5393");
        app.UseHealthChecks("/by-port", 5393);
        await app.StartAsync();

        using var client = new HttpClient();
        foreach (string path in new[] { "/by-host", "/by-port" })
        {
            Console.WriteLine($"K12 {path} on 5392 -> {await StatusAsync(client, "http://localhost:5392" + path, host: null)}");
            Console.WriteLine($"K12 {path} on 5392 with Host: localhost:5393 -> {await StatusAsync(client, "http://localhost:5392" + path, host: "localhost:5393")}");
            Console.WriteLine($"K12 {path} on 5393 -> {await StatusAsync(client, "http://localhost:5393" + path, host: null)}");
        }

        await app.StopAsync();

        static async Task<int> StatusAsync(HttpClient client, string url, string? host)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (host is not null)
            {
                request.Headers.Host = host;
            }

            using HttpResponseMessage response = await client.SendAsync(request);
            return (int)response.StatusCode;
        }
    }

    private static async Task RunCaseAsync(
        string label,
        Action<IHealthChecksBuilder> register,
        Action<IServiceCollection>? services = null)
    {
        var collection = new ServiceCollection();
        collection.AddLogging();
        services?.Invoke(collection);
        register(collection.AddHealthChecks());
        await using ServiceProvider provider = collection.BuildServiceProvider();
        HealthCheckService service = provider.GetRequiredService<HealthCheckService>();

        var stopwatch = Stopwatch.StartNew();
        HealthReport report = await service.CheckHealthAsync();
        double seconds = Math.Round(stopwatch.Elapsed.TotalSeconds, 1);
        string entries = string.Join("; ", report.Entries.Select(e =>
            $"{e.Key}={e.Value.Status} description=\"{e.Value.Description}\" exception={e.Value.Exception?.GetType().Name ?? "-"}"));
        Console.WriteLine($"{label} -> {report.Status} in {seconds:0.0} s {(entries.Length > 0 ? "| " + entries : "")}");
    }

    private static string Sqlite(string path) =>
        new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString();

    // The delegate overloads of AddCheck take no failureStatus, so K4 and K5 register an instance.
    private sealed class FuncCheck(Func<HealthCheckResult> check) : IHealthCheck
    {
        public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult(check());
    }

    private sealed class ProbeDb(DbContextOptions<ProbeDb> options) : DbContext(options);
}
