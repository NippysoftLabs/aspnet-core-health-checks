using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace ProbeLab;

// Runs against the real app in its own process, with the fake payments API on localhost:5399.
public static class LiveRuns
{
    private static readonly HttpClient Client = new(new SocketsHttpHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(30),
    };

    public static async Task RunAsync(string appDll)
    {
        await using PaymentsStub stub = await PaymentsStub.StartAsync("http://localhost:5399");

        Console.WriteLine("# Run A: CacheSeconds=0, CheckTimeoutMs=800, WarmUpSeconds=3");
        await using (AppProcess app = AppProcess.Start(appDll, new Dictionary<string, string>
        {
            ["Payments__CacheSeconds"] = "0",
            ["Payments__CheckTimeoutMs"] = "800",
            ["Startup__WarmUpSeconds"] = "3",
        }))
        {
            await app.WaitForListeningAsync(Client);
            await StartupTimelineAsync(app);
            await BindingsAsync(app);
            await PaymentsStatesAsync(stub);
            await KubeletAgainstHangAsync(stub, "P5");
            await PortsAsync();
            await VerbsAndPathsAsync();
            await HeadersAsync();
            await FloodAsync(stub, "P11");
            await AbortAsync(stub);
        }

        Console.WriteLine();
        Console.WriteLine("# Run B: CacheSeconds=10, CheckTimeoutMs=800, WarmUpSeconds=0");
        await using (AppProcess app = AppProcess.Start(appDll, new Dictionary<string, string>
        {
            ["Payments__CacheSeconds"] = "10",
            ["Payments__CheckTimeoutMs"] = "800",
            ["Startup__WarmUpSeconds"] = "0",
        }))
        {
            await app.WaitForListeningAsync(Client);
            await Task.Delay(500);
            await FloodAsync(stub, "P13");
            await StaleCacheAsync(stub);
            await CacheWhileHangingAsync(stub);
        }

        Console.WriteLine();
        Console.WriteLine("# Run C: CacheSeconds=0, CheckTimeoutMs=2000, WarmUpSeconds=0");
        await using (AppProcess app = AppProcess.Start(appDll, new Dictionary<string, string>
        {
            ["Payments__CacheSeconds"] = "0",
            ["Payments__CheckTimeoutMs"] = "2000",
            ["Startup__WarmUpSeconds"] = "0",
        }))
        {
            await app.WaitForListeningAsync(Client);
            await Task.Delay(500);
            await KubeletAgainstHangAsync(stub, "P15");
        }
    }

    private static async Task StartupTimelineAsync(AppProcess app)
    {
        Console.WriteLine("## P1 startup: liveness, startup and readiness while the warm-up runs");
        for (int i = 0; i < 12; i++)
        {
            (int liveCode, string liveBody) = await GetAsync(AppProcess.PublicUrl + "/health/live");
            (int startupCode, string startupBody) = await GetAsync(AppProcess.PublicUrl + "/health/startup");
            (int readyCode, string readyBody) = await GetAsync(AppProcess.PublicUrl + "/health/ready");
            Console.WriteLine($"P1 t={Bucket(app.SinceStart.Elapsed)} live -> {liveCode} {liveBody}; startup -> {startupCode} {startupBody}; ready -> {readyCode} {readyBody}");
            if (readyCode == 200)
            {
                break;
            }

            await Task.Delay(500);
        }
    }

    private static async Task PaymentsStatesAsync(PaymentsStub stub)
    {
        foreach ((string label, string mode) in new[] { ("P2", "ok"), ("P3", "fail"), ("P4", "hang") })
        {
            stub.Mode = mode;
            Console.WriteLine($"## {label} payments stub mode={mode}");
            var stopwatch = Stopwatch.StartNew();
            (int code, string body) = await GetAsync(AppProcess.PublicUrl + "/health/ready");
            Console.WriteLine($"{label} ready -> {code} {body} in {Seconds(stopwatch.Elapsed)}");
            (code, body) = await GetAsync(AppProcess.ManagementUrl + "/health/details");
            Console.WriteLine($"{label} details -> {code} {WithoutDurations(body)}");
        }

        stub.Mode = "ok";
    }

    private static async Task KubeletAgainstHangAsync(PaymentsStub stub, string label)
    {
        stub.Mode = "hang";
        Console.WriteLine($"## {label} kubelet rules (timeoutSeconds=1, failureThreshold=3) while payments hangs");
        await KubeletSim.RunAsync(AppProcess.PublicUrl, new ProbeSpec("readiness", "/health/ready"), attempts: 4);
        await KubeletSim.RunAsync(AppProcess.PublicUrl, new ProbeSpec("liveness", "/health/live"), attempts: 2);
        stub.Mode = "ok";
    }

    // P17: which addresses Kestrel bound, and whether each port answers on a non-loopback address of this machine.
    // The address itself is not printed.
    private static async Task BindingsAsync(AppProcess app)
    {
        Console.WriteLine("## P17 bindings: public port on every interface, management port on localhost only");
        foreach (string line in app.ListeningOn)
        {
            Console.WriteLine($"P17 {line}");
        }

        IPAddress? lan = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Select(a => a.Address)
            .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a));
        if (lan is null)
        {
            Console.WriteLine("P17 no non-loopback IPv4 address on this machine; skipped");
            return;
        }

        foreach ((string label, int port, string path) in new[] { ("public", 5390, "/health/live"), ("management", 5391, "/health/details") })
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            string outcome;
            try
            {
                using HttpResponseMessage response = await Client.GetAsync($"http://{lan}:{port}{path}", cts.Token);
                outcome = ((int)response.StatusCode).ToString();
            }
            catch (HttpRequestException exception)
            {
                outcome = $"connection failed ({exception.HttpRequestError})";
            }

            Console.WriteLine($"P17 {label} port {port} on a non-loopback address of this machine -> {outcome}");
        }
    }

    // P16: the cache does not keep a call cancelled by the timeout, so while payments hangs every probe calls it.
    private static async Task CacheWhileHangingAsync(PaymentsStub stub)
    {
        stub.Mode = "hang";
        await Task.Delay(TimeSpan.FromSeconds(10.5));
        stub.ResetCounters();
        Console.WriteLine("## P16 CacheSeconds=10 while payments hangs: 10 requests, one per second");
        var answers = new List<string>();
        for (int i = 0; i < 10; i++)
        {
            (int code, string body) = await GetAsync(AppProcess.PublicUrl + "/health/ready");
            answers.Add($"{code} {body}");
            await Task.Delay(1000);
        }

        string summary = string.Join(", ", answers.GroupBy(a => a).Select(g => $"{g.Key} x{g.Count()}"));
        Console.WriteLine($"P16 answers: {summary}; calls that reached payments-api: {stub.Hits}");
        stub.Mode = "ok";
    }

    private static async Task PortsAsync()
    {
        Console.WriteLine("## P6 /health/details by port");
        Console.WriteLine($"P6 management port -> {(await GetAsync(AppProcess.ManagementUrl + "/health/details")).Code}");
        Console.WriteLine($"P6 public port -> {(await GetAsync(AppProcess.PublicUrl + "/health/details")).Code}");
        (int code, _) = await SendAsync(HttpMethod.Get, AppProcess.PublicUrl + "/health/details", host: "localhost:5391");
        Console.WriteLine($"P6 public port with Host: localhost:5391 -> {code}");
    }

    private static async Task VerbsAndPathsAsync()
    {
        Console.WriteLine("## P7 other HTTP methods on /health/ready");
        foreach (HttpMethod method in new[] { HttpMethod.Post, HttpMethod.Put, HttpMethod.Delete, HttpMethod.Head, HttpMethod.Options })
        {
            (int code, string body) = await SendAsync(method, AppProcess.PublicUrl + "/health/ready");
            Console.WriteLine($"P7 {method} -> {code} {body}");
        }

        Console.WriteLine("## P8 path variants");
        string[] paths =
        [
            AppProcess.PublicUrl + "/HEALTH/READY",
            AppProcess.PublicUrl + "/health/ready/",
            AppProcess.PublicUrl + "/health/ready?verbose=true",
            AppProcess.PublicUrl + "/health/ready/extra",
            AppProcess.PublicUrl + "/health",
            AppProcess.ManagementUrl + "/HEALTH/DETAILS",
            AppProcess.ManagementUrl + "/health/details/",
            AppProcess.ManagementUrl + "/health/details/extra",
        ];
        foreach (string url in paths)
        {
            (int code, string body) = await GetAsync(url);
            Console.WriteLine($"P8 {new Uri(url).Port} {new Uri(url).PathAndQuery} -> {code} {Shorten(body)}");
        }
    }

    private static async Task HeadersAsync()
    {
        Console.WriteLine("## P9 response headers of /health/ready");
        using var request = new HttpRequestMessage(HttpMethod.Get, AppProcess.PublicUrl + "/health/ready");
        using HttpResponseMessage response = await Client.SendAsync(request);
        Console.WriteLine($"P9 Content-Type: {response.Content.Headers.ContentType}");
        Console.WriteLine($"P9 Cache-Control: {response.Headers.CacheControl}");
        Console.WriteLine($"P9 Pragma: {string.Join(",", response.Headers.Pragma)}");
        Console.WriteLine($"P9 Expires: {response.Content.Headers.Expires:R}");

        Console.WriteLine("## P10 Accept: application/json on /health/ready");
        using var json = new HttpRequestMessage(HttpMethod.Get, AppProcess.PublicUrl + "/health/ready");
        json.Headers.Accept.ParseAdd("application/json");
        using HttpResponseMessage jsonResponse = await Client.SendAsync(json);
        Console.WriteLine($"P10 -> {(int)jsonResponse.StatusCode} {jsonResponse.Content.Headers.ContentType} {await jsonResponse.Content.ReadAsStringAsync()}");
    }

    private static async Task FloodAsync(PaymentsStub stub, string label)
    {
        stub.Mode = "ok";
        stub.ResetCounters();
        Console.WriteLine($"## {label} 200 concurrent GET /health/ready");
        var stopwatch = Stopwatch.StartNew();
        (int Code, string Body)[] results = await Task.WhenAll(
            Enumerable.Range(0, 200).Select(_ => GetAsync(AppProcess.PublicUrl + "/health/ready")));
        string summary = string.Join(", ", results.GroupBy(r => $"{r.Code} {r.Body}").OrderBy(g => g.Key).Select(g => $"{g.Key} x{g.Count()}"));
        Console.WriteLine($"{label} answers: {summary}; calls that reached payments-api: {stub.Hits}");
    }

    private static async Task AbortAsync(PaymentsStub stub)
    {
        stub.Mode = "hang";
        stub.ResetCounters();
        Console.WriteLine("## P12 client gives up after 200 ms while payments hangs (5 requests)");
        for (int i = 0; i < 5; i++)
        {
            using var cts = new CancellationTokenSource(200);
            try
            {
                await Client.GetAsync(AppProcess.PublicUrl + "/health/ready", cts.Token);
            }
            catch (OperationCanceledException)
            {
                // Expected: the client stopped waiting.
            }
        }

        await Task.Delay(1500);
        Console.WriteLine($"P12 calls that reached payments-api: {stub.Hits}; cancelled: {stub.Aborted}, after {string.Join(", ", stub.AbortedAfterSeconds.Select(s => $"{s:0.0} s"))}");
        stub.Mode = "ok";
    }

    private static async Task StaleCacheAsync(PaymentsStub stub)
    {
        Console.WriteLine("## P14 cached result while payments starts failing");
        stub.Mode = "ok";
        var stopwatch = Stopwatch.StartNew();
        Console.WriteLine($"P14 t=0.0 s stub=ok ready -> {await ReadyAsync()}");
        stub.Mode = "fail";
        Console.WriteLine($"P14 t={Seconds(stopwatch.Elapsed)} stub=fail ready -> {await ReadyAsync()}");
        await Task.Delay(TimeSpan.FromSeconds(10.5) - stopwatch.Elapsed);
        Console.WriteLine($"P14 t={Bucket(stopwatch.Elapsed)} stub=fail ready -> {await ReadyAsync()}");
        stub.Mode = "ok";

        static async Task<string> ReadyAsync()
        {
            (int code, string body) = await GetAsync(AppProcess.PublicUrl + "/health/ready");
            return $"{code} {body}";
        }
    }

    private static Task<(int Code, string Body)> GetAsync(string url) => SendAsync(HttpMethod.Get, url);

    private static async Task<(int Code, string Body)> SendAsync(HttpMethod method, string url, string? host = null)
    {
        using var request = new HttpRequestMessage(method, url);
        if (host is not null)
        {
            request.Headers.Host = host;
        }

        using HttpResponseMessage response = await Client.SendAsync(request);
        return ((int)response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private static string Seconds(TimeSpan elapsed) => $"{elapsed.TotalSeconds:0.0} s";

    // Rounds to 0.5 s so the output does not change with every run.
    private static string Bucket(TimeSpan elapsed) => $"{Math.Round(elapsed.TotalSeconds * 2, MidpointRounding.AwayFromZero) / 2:0.0} s";

    private static string WithoutDurations(string json) =>
        System.Text.RegularExpressions.Regex.Replace(json, "\"(totalDurationMs|durationMs)\":\\d+(\\.\\d+)?", "\"$1\":_");

    private static string Shorten(string body) => body.Length <= 60 ? body : body[..60] + "...";
}
