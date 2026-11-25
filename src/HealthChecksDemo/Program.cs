using HealthChecksDemo;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

var builder = WebApplication.CreateBuilder(args);

// Port that serves the detailed JSON report. The middleware compares it with the local port
// of the connection, not with the Host header, so a client cannot pick it by sending a header.
int managementPort = builder.Configuration.GetValue("Management:Port", 8081);

string databasePath = Path.GetFullPath(
    builder.Configuration.GetValue("Orders:DatabasePath", "data/orders.db")!,
    builder.Environment.ContentRootPath);
Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
string sqlite = new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString();
builder.Services.AddDbContext<OrdersDb>(options => options.UseSqlite(sqlite));

builder.Services.AddSingleton<StartupGate>();
builder.Services.AddHostedService<WarmUpService>();

PaymentsOptions payments = builder.Configuration.GetSection("Payments").Get<PaymentsOptions>() ?? new PaymentsOptions();
builder.Services.AddSingleton(payments);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<PaymentsHealthCache>();
builder.Services.AddHttpClient(PaymentsApiHealthCheck.ClientName, client => client.BaseAddress = new Uri(payments.BaseUrl));

builder.Services.AddHealthChecks()
    // Fails until the warm-up work has finished.
    .AddCheck<StartupHealthCheck>("startup", tags: ["ready"])
    // EF Core check: calls Database.CanConnectAsync on OrdersDb.
    .AddDbContextCheck<OrdersDb>("database", tags: ["ready"])
    // A payments outage reports Degraded, not Unhealthy, so it does not take every instance
    // out of the load balancer at once. The timeout cancels the call after CheckTimeoutMs.
    .AddCheck<PaymentsApiHealthCheck>(
        "payments-api",
        failureStatus: HealthStatus.Degraded,
        tags: ["ready"],
        timeout: TimeSpan.FromMilliseconds(payments.CheckTimeoutMs));

var app = builder.Build();

// Liveness: runs no checks. It answers 200 "Healthy" while the process can serve HTTP.
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });

// Readiness: runs the checks tagged "ready". Unhealthy -> 503; Healthy and Degraded -> 200 (defaults).
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = registration => registration.Tags.Contains("ready"),
});

// Every check with a JSON body, only for connections that arrived on the management port.
app.UseHealthChecks("/health/details", managementPort, new HealthCheckOptions
{
    ResponseWriter = HealthReportJson.WriteAsync,
});

app.MapGet("/orders/count", async (OrdersDb db, CancellationToken ct) => await db.Orders.CountAsync(ct));

app.Run();

// Lets the test project start the app with WebApplicationFactory<Program>.
public partial class Program;
