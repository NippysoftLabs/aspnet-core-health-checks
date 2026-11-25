using System.Collections.Concurrent;
using System.Diagnostics;

namespace ProbeLab;

// Fake payments API for the runs. GET /status answers according to Mode:
// "ok" -> 200, "fail" -> 500, "hang" -> never answers until the caller gives up.
public sealed class PaymentsStub : IAsyncDisposable
{
    private readonly WebApplication _app;
    private int _hits;
    private int _aborted;
    private readonly ConcurrentQueue<double> _abortedAfterSeconds = new();

    public volatile string Mode = "ok";

    public int Hits => Volatile.Read(ref _hits);

    public int Aborted => Volatile.Read(ref _aborted);

    // How long each hanging call waited before the caller cancelled it.
    public IReadOnlyCollection<double> AbortedAfterSeconds => _abortedAfterSeconds;

    private PaymentsStub(WebApplication app) => _app = app;

    public static async Task<PaymentsStub> StartAsync(string url)
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls(url);
        builder.Logging.ClearProviders();
        WebApplication app = builder.Build();
        var stub = new PaymentsStub(app);

        app.MapGet("/status", async (HttpContext context) =>
        {
            Interlocked.Increment(ref stub._hits);
            switch (stub.Mode)
            {
                case "fail":
                    return Results.StatusCode(StatusCodes.Status500InternalServerError);
                case "hang":
                    var waited = Stopwatch.StartNew();
                    try
                    {
                        await Task.Delay(Timeout.Infinite, context.RequestAborted);
                    }
                    catch (OperationCanceledException)
                    {
                        Interlocked.Increment(ref stub._aborted);
                        stub._abortedAfterSeconds.Enqueue(Math.Round(waited.Elapsed.TotalSeconds, 1));
                    }
                    return Results.Empty;
                default:
                    return Results.Ok();
            }
        });

        await app.StartAsync();
        return stub;
    }

    public void ResetCounters()
    {
        Interlocked.Exchange(ref _hits, 0);
        Interlocked.Exchange(ref _aborted, 0);
        _abortedAfterSeconds.Clear();
    }

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();
}
