using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace HealthChecksDemo;

public static class HealthReportJson
{
    public sealed record CheckView(string Name, string Status, double DurationMs, string? Description);

    public sealed record ReportView(string Status, double TotalDurationMs, IReadOnlyList<CheckView> Checks);

    public static Task WriteAsync(HttpContext context, HealthReport report)
    {
        var view = new ReportView(
            report.Status.ToString(),
            Math.Round(report.TotalDuration.TotalMilliseconds),
            report.Entries
                .Select(entry => new CheckView(
                    entry.Key,
                    entry.Value.Status.ToString(),
                    Math.Round(entry.Value.Duration.TotalMilliseconds),
                    Describe(entry.Value)))
                .ToList());

        return context.Response.WriteAsJsonAsync(view, context.RequestAborted);
    }

    // Exception messages can carry SQL text, file paths or host names, so they never reach the response.
    // A check that failed with an exception shows only the exception type; the full exception goes to the log.
    private static string? Describe(HealthReportEntry entry) => entry.Exception switch
    {
        null => entry.Description,
        OperationCanceledException => "timed out or canceled",
        Exception exception => $"failed with {exception.GetType().Name}",
    };
}
