using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace HealthChecksDemo;

public sealed class OrdersDb(DbContextOptions<OrdersDb> options) : DbContext(options)
{
    public DbSet<Order> Orders => Set<Order>();
}

public sealed class Order
{
    public int Id { get; set; }
    public long TotalCents { get; set; }
}

// Set once by WarmUpService; read by StartupHealthCheck on every readiness probe.
public sealed class StartupGate
{
    private volatile bool _ready;

    public bool IsReady => _ready;

    public void MarkReady() => _ready = true;
}

public sealed class WarmUpService(
    StartupGate gate,
    IServiceScopeFactory scopes,
    IConfiguration configuration) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Creates the database file and its table when they do not exist yet.
        await using (AsyncServiceScope scope = scopes.CreateAsyncScope())
        {
            OrdersDb db = scope.ServiceProvider.GetRequiredService<OrdersDb>();
            await db.Database.EnsureCreatedAsync(stoppingToken);
        }

        // Stands in for slow startup work such as filling caches. The length comes from configuration.
        int seconds = configuration.GetValue("Startup:WarmUpSeconds", 5);
        await Task.Delay(TimeSpan.FromSeconds(seconds), stoppingToken);

        gate.MarkReady();
    }
}

public sealed class StartupHealthCheck(StartupGate gate) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(gate.IsReady
            ? HealthCheckResult.Healthy("warm-up finished")
            : new HealthCheckResult(context.Registration.FailureStatus, "warm-up still running"));
}
