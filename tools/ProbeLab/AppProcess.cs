using System.Diagnostics;

namespace ProbeLab;

// Runs the built HealthChecksDemo.dll as a separate process with the given settings.
public sealed class AppProcess : IAsyncDisposable
{
    public const string PublicUrl = "http://localhost:5390";
    public const string ManagementUrl = "http://localhost:5391";

    private readonly Process _process;
    private readonly string _dataDirectory;

    public Stopwatch SinceStart { get; } = new();

    private AppProcess(Process process, string dataDirectory)
    {
        _process = process;
        _dataDirectory = dataDirectory;
    }

    public static AppProcess Start(string appDll, IDictionary<string, string> settings)
    {
        string dataDirectory = Directory.CreateTempSubdirectory("hc-probe-").FullName;
        var info = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = Path.GetDirectoryName(appDll)!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        info.ArgumentList.Add(appDll);
        info.Environment["Urls"] = $"{PublicUrl};{ManagementUrl}";
        info.Environment["Management__Port"] = "5391";
        info.Environment["Orders__DatabasePath"] = Path.Combine(dataDirectory, "orders.db");
        info.Environment["Logging__LogLevel__Default"] = "Warning";
        foreach ((string key, string value) in settings)
        {
            info.Environment[key] = value;
        }

        var process = new Process { StartInfo = info };
        var app = new AppProcess(process, dataDirectory);
        process.OutputDataReceived += (_, _) => { };
        process.ErrorDataReceived += (_, _) => { };
        process.Start();
        app.SinceStart.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return app;
    }

    // Waits until Kestrel accepts connections on the public port.
    public async Task WaitForListeningAsync(HttpClient client)
    {
        for (int i = 0; i < 200; i++)
        {
            try
            {
                using HttpResponseMessage response = await client.GetAsync(PublicUrl + "/health/live");
                return;
            }
            catch (HttpRequestException)
            {
                await Task.Delay(50);
            }
        }

        throw new TimeoutException("The app did not start listening.");
    }

    public async ValueTask DisposeAsync()
    {
        if (!_process.HasExited)
        {
            _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync();
        }

        _process.Dispose();
        try
        {
            Directory.Delete(_dataDirectory, recursive: true);
        }
        catch (IOException)
        {
            // The SQLite file can stay locked for a moment after the process exits; the folder is in the temp directory.
        }
    }
}
