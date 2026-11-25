using System.Diagnostics;
using System.Net.Http.Headers;

namespace ProbeLab;

// Applies the documented kubelet rules for an httpGet probe. It is not Kubernetes:
// - GET with User-Agent "kube-probe/<version>" and Accept "*/*";
// - each attempt gives up after TimeoutSeconds (Kubernetes default: 1);
// - success when 200 <= status < 400, anything else or a timeout is a failure;
// - FailureThreshold failures in a row (default: 3) flip the probe to failed.
// The period between attempts is shortened to PauseMs so a run takes seconds, not minutes.
public sealed record ProbeSpec(string Kind, string Path, int TimeoutSeconds = 1, int FailureThreshold = 3);

public static class KubeletSim
{
    private static readonly HttpClient Client = new(new SocketsHttpHandler { AllowAutoRedirect = false })
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };

    public static async Task RunAsync(string baseUrl, ProbeSpec spec, int attempts, int pauseMs = 1000)
    {
        int failuresInARow = 0;
        for (int attempt = 1; attempt <= attempts; attempt++)
        {
            var stopwatch = Stopwatch.StartNew();
            string outcome;
            bool success;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(spec.TimeoutSeconds));
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, baseUrl + spec.Path);
                request.Headers.UserAgent.Add(new ProductInfoHeaderValue("kube-probe", "1.37"));
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));
                using HttpResponseMessage response = await Client.SendAsync(request, timeout.Token);
                int code = (int)response.StatusCode;
                success = code is >= 200 and < 400;
                outcome = $"{code} {await response.Content.ReadAsStringAsync(timeout.Token)}";
            }
            catch (OperationCanceledException)
            {
                success = false;
                outcome = $"no answer within timeoutSeconds={spec.TimeoutSeconds}";
            }

            failuresInARow = success ? 0 : failuresInARow + 1;
            string state = failuresInARow >= spec.FailureThreshold
                ? (spec.Kind == "liveness" ? "FAILED -> kubelet restarts the container" : "FAILED -> Pod Ready=false, removed from Service endpoints")
                : (success ? "Success" : $"Failure {failuresInARow}/{spec.FailureThreshold}");
            Console.WriteLine($"  {spec.Kind} attempt {attempt}: {outcome} after {stopwatch.Elapsed.TotalSeconds:0.0} s -> {state}");
            await Task.Delay(pauseMs);
        }
    }
}
