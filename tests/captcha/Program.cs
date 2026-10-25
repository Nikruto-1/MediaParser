using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using MediaParser.Services.Captcha;
using Microsoft.Extensions.Options;

// This executable has no real HTTP transport: every solver receives a fake HttpMessageHandler.
int passed = 0, failed = 0;
var recaptcha = new CaptchaChallenge(CaptchaKind.RecaptchaV2, "https://example.test/alice", "public-test-sitekey", Callback: "page.onCaptcha", Invisible: true);
var turnstile = new CaptchaChallenge(CaptchaKind.Turnstile, "https://example.test/alice", "public-turnstile-sitekey", Action: "signup", Data: "test-custom-data");
CaptchaOptions Enabled(int timeout = 30, int poll = 5) => new() { Enabled = true, ApiKey = "dummy-test-key-not-valid", TimeoutSeconds = timeout, PollIntervalSeconds = poll };
void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
void Check(string name, Action action)
{
    try { action(); Interlocked.Increment(ref passed); Console.WriteLine($"PASS {name}"); }
    catch (Exception ex) { Interlocked.Increment(ref failed); Console.Error.WriteLine($"FAIL {name}: {ex.GetBaseException().Message}"); }
}
async Task Test(string name, Func<Task> action)
{
    try { await action(); Interlocked.Increment(ref passed); Console.WriteLine($"PASS {name}"); }
    catch (Exception ex) { Interlocked.Increment(ref failed); Console.Error.WriteLine($"FAIL {name}: {ex.GetBaseException().Message}"); }
}
HttpResponseMessage Json(string value) => new(HttpStatusCode.OK) { Content = new StringContent(value, Encoding.UTF8, "application/json") };
TwoCaptchaClient Client(FakeHandler handler, CaptchaOptions settings) => new(new HttpClient(handler) { BaseAddress = new Uri("https://untrusted.example.invalid/") }, Options.Create(settings));
void Failed(CaptchaSolution result, string code)
{
    Assert(!result.Success && result.Token == null, "Failed solves must not expose a token.");
    Assert(result.ErrorCode == code, $"Expected controlled error code {code}.");
    Assert(!result.Message.Contains("dummy-test-key", StringComparison.Ordinal), "A credential leaked into a diagnostic.");
}

Check("The per-search budget permits exactly its cap under concurrency", () =>
{
    var budget = new CaptchaSolveBudget(3);
    int reserved = 0;
    Parallel.For(0, 1000, _ => { if (budget.TryReserve()) Interlocked.Increment(ref reserved); });
    Assert(reserved == 3 && budget.Used == 3 && budget.MaxTasks == 3 && !budget.TryReserve(), "Budget exceeded its cap.");
});
Check("Zero and negative budgets never reserve a paid task", () =>
{
    foreach (int cap in new[] { 0, -1 }) { var budget = new CaptchaSolveBudget(cap); Assert(!budget.TryReserve() && budget.Used == 0, "An empty budget was consumed."); }
});

var tests = new List<Task>
{
    Test("Disabled solver performs no HTTP requests", async () =>
    {
        var handler = new FakeHandler((_, _, _) => throw new InvalidOperationException("HTTP must not run."));
        var client = Client(handler, new CaptchaOptions());
        Assert(!client.IsAvailable, "Disabled solver advertised availability.");
        Failed(await client.SolveAsync(recaptcha, CancellationToken.None), "DISABLED"); Assert(handler.Calls == 0, "Disabled solver contacted HTTP.");
    }),
    Test("Missing API key performs no HTTP requests", async () =>
    {
        var handler = new FakeHandler((_, _, _) => throw new InvalidOperationException("HTTP must not run."));
        var client = Client(handler, new CaptchaOptions { Enabled = true, ApiKey = " " });
        Assert(!client.IsAvailable, "Missing key advertised availability.");
        Failed(await client.SolveAsync(recaptcha, CancellationToken.None), "MISSING_API_KEY"); Assert(handler.Calls == 0, "Missing-key solve contacted HTTP.");
    }),
    Test("Invalid challenge URL and missing sitekey never submit a task", async () =>
    {
        var handler = new FakeHandler((_, _, _) => throw new InvalidOperationException("HTTP must not run."));
        var client = Client(handler, Enabled());
        foreach (var challenge in new[] { recaptcha with { WebsiteUrl = "file:///tmp/challenge" }, recaptcha with { SiteKey = " " }, recaptcha with { WebsiteUrl = "https://user:password@example.test/" } })
            Failed(await client.SolveAsync(challenge, CancellationToken.None), "INVALID_CHALLENGE");
        Assert(handler.Calls == 0, "Malformed challenge contacted HTTP.");
    }),
    Test("Unsupported captcha kind never submits a task", async () =>
    {
        var handler = new FakeHandler((_, _, _) => throw new InvalidOperationException("HTTP must not run."));
        Failed(await Client(handler, Enabled()).SolveAsync(recaptcha with { Kind = (CaptchaKind)99 }, CancellationToken.None), "UNSUPPORTED_CAPTCHA");
        Assert(handler.Calls == 0, "Unsupported challenge contacted HTTP.");
    }),
    Test("Private URL credentials, fragments and sensitive query values are never sent to the provider", async () =>
    {
        var handler = new FakeHandler((_, _, _) => throw new InvalidOperationException("Private page URL must not reach HTTP."));
        var client = Client(handler, Enabled());
        foreach (var suffix in new[] { "#private-fragment", "?token=private", "?access_token=private", "?ID_TOKEN=private", "?code=private", "?api_key=private", "?apikey=private", "?key=private", "?password=private", "?secret=private", "?session=private", "?auth=private", "?signature=private", "?%61ccess%5ftoken=private", "?filter=public;token=private", "?token[]=private" })
            Failed(await client.SolveAsync(recaptcha with { WebsiteUrl = "https://example.test/profile" + suffix }, CancellationToken.None), "INVALID_CHALLENGE");
        Assert(handler.Calls == 0, "A URL containing private data contacted the provider.");
    }),
    Test("Pre-cancelled solve submits no task", async () =>
    {
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        var handler = new FakeHandler((_, _, _) => throw new InvalidOperationException("HTTP must not run."));
        Failed(await Client(handler, Enabled()).SolveAsync(recaptcha, cancelled.Token), "CANCELLED"); Assert(handler.Calls == 0, "Cancelled solve contacted HTTP.");
    }),
    Test("Create task uses the fixed API host and is never retried after network failure", async () =>
    {
        var handler = new FakeHandler((request, _, _) =>
        {
            Assert(request.RequestUri!.AbsoluteUri == "https://api.2captcha.com/createTask", "A configured BaseAddress changed the API endpoint.");
            throw new HttpRequestException("Do not display dummy-test-key-not-valid or C:\\secret\\profile.");
        });
        Failed(await Client(handler, Enabled()).SolveAsync(recaptcha, CancellationToken.None), "NETWORK_ERROR"); Assert(handler.Calls == 1, "A chargeable request was retried.");
    }),
    Test("HTTP errors do not echo the response body or trigger retries", async () =>
    {
        var handler = new FakeHandler((_, _, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("dummy-test-key-not-valid") }));
        Failed(await Client(handler, Enabled()).SolveAsync(recaptcha, CancellationToken.None), "HTTP_ERROR"); Assert(handler.Calls == 1, "HTTP error retried task creation.");
    }),
    Test("Redirect response is rejected instead of constructing another endpoint", async () =>
    {
        var handler = new FakeHandler((_, _, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TemporaryRedirect);
            response.Headers.Location = new Uri("https://untrusted.example.invalid/steal");
            return Task.FromResult(response);
        });
        Failed(await Client(handler, Enabled()).SolveAsync(recaptcha, CancellationToken.None), "HTTP_ERROR"); Assert(handler.Calls == 1, "Redirect caused another request.");
    }),
    Test("Unknown provider errors and descriptions are sanitized", async () =>
    {
        var handler = new FakeHandler((_, _, _) => Task.FromResult(Json("{\"errorId\":99,\"errorCode\":\"dummy-test-key-not-valid\",\"errorDescription\":\"C:\\\\secret\\\\profile\"}")));
        var result = await Client(handler, Enabled()).SolveAsync(recaptcha, CancellationToken.None);
        Failed(result, "PROVIDER_ERROR"); Assert(!result.Message.Contains("secret", StringComparison.Ordinal), "Provider details leaked.");
    }),
    Test("Malformed provider JSON is a controlled failure without duplicate submission", async () =>
    {
        var handler = new FakeHandler((_, _, _) => Task.FromResult(Json("<html>dummy-test-key-not-valid</html>")));
        Failed(await Client(handler, Enabled()).SolveAsync(recaptcha, CancellationToken.None), "INVALID_RESPONSE"); Assert(handler.Calls == 1, "Malformed JSON retried task creation.");
    }),
    Test("Cancellation aborts an in-flight createTask without resubmitting", async () =>
    {
        var handler = new FakeHandler(async (_, _, token) => { await Task.Delay(30000, token); return Json("{}"); });
        using var cancelled = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        Failed(await Client(handler, Enabled()).SolveAsync(recaptcha, cancelled.Token), "CANCELLED"); Assert(handler.Calls == 1, "Cancelled submission was retried.");
    }),
    Test("A deadline aborts an in-flight request and differs from user cancellation", async () =>
    {
        var handler = new FakeHandler(async (_, _, token) => { await Task.Delay(30000, token); return Json("{}"); });
        Failed(await Client(handler, Enabled(timeout: 1)).SolveAsync(recaptcha, CancellationToken.None), "TIMEOUT"); Assert(handler.Calls == 1, "Timed-out submission was retried.");
    }),
    Test("Cancellation during the polling delay does not create a second task", async () =>
    {
        var handler = new FakeHandler((_, _, _) => Task.FromResult(Json("{\"errorId\":0,\"taskId\":12345678901}")));
        using var cancelled = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        Failed(await Client(handler, Enabled()).SolveAsync(recaptcha, cancelled.Token), "CANCELLED"); Assert(handler.Calls == 1, "Cancellation reached a poll or a duplicate task.");
    })
};
foreach (var (provider, expected) in new[]
{
    ("ERROR_KEY_DOES_NOT_EXIST", "ERROR_KEY_DOES_NOT_EXIST"), ("ERROR_ZERO_BALANCE", "ERROR_ZERO_BALANCE"),
    ("ERROR_IP_NOT_ALLOWED", "ERROR_IP_NOT_ALLOWED"), ("ERROR_TASK_NOT_SUPPORTED", "ERROR_TASK_NOT_SUPPORTED"),
    ("ERROR_ACCOUNT_SUSPENDED", "ERROR_ACCOUNT_SUSPENDED"), ("ERROR_BAD_PARAMETERS", "INVALID_CHALLENGE"),
    ("ERROR_CAPTCHA_UNSOLVABLE", "ERROR_CAPTCHA_UNSOLVABLE"), ("ERROR_NO_SLOT_AVAILABLE", "ERROR_NO_SLOT_AVAILABLE")
})
    tests.Add(Test($"Provider error {provider} has a controlled Ukrainian diagnostic", async () =>
    {
        var handler = new FakeHandler((_, _, _) => Task.FromResult(Json(JsonSerializer.Serialize(new { errorId = 1, errorCode = provider, errorDescription = "dummy-test-key-not-valid" }))));
        Failed(await Client(handler, Enabled()).SolveAsync(recaptcha, CancellationToken.None), expected); Assert(handler.Calls == 1, "Account error retried task creation.");
    }));

tests.Add(Test("reCAPTCHA processing/ready uses documented JSON, one task and polling >=5 seconds", async () =>
{
    var stopwatch = Stopwatch.StartNew();
    var timestamps = new List<TimeSpan>();
    var handler = new FakeHandler(async (request, call, token) =>
    {
        Assert(request.Method == HttpMethod.Post, "API method must be POST.");
        Assert(request.RequestUri!.Host == "api.2captcha.com" && request.RequestUri.Scheme == "https", "Untrusted endpoint used.");
        using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
        Assert(body.RootElement.GetProperty("clientKey").GetString() == "dummy-test-key-not-valid", "Incorrect credential field.");
        timestamps.Add(stopwatch.Elapsed);
        if (call == 1)
        {
            Assert(request.RequestUri.AbsolutePath == "/createTask", "Expected createTask exactly once.");
            var task = body.RootElement.GetProperty("task");
            Assert(task.GetProperty("type").GetString() == "RecaptchaV2TaskProxyless", "Wrong reCAPTCHA task type.");
            Assert(task.GetProperty("websiteURL").GetString() == recaptcha.WebsiteUrl && task.GetProperty("websiteKey").GetString() == recaptcha.SiteKey, "Wrong page parameters.");
            Assert(task.GetProperty("isInvisible").GetBoolean(), "Invisible flag lost.");
            Assert(!body.RootElement.TryGetProperty("callbackUrl", out _) && !task.TryGetProperty("Callback", out _), "A JS callback was transmitted as a webhook.");
            return Json("{\"errorId\":0,\"taskId\":12345678901}");
        }
        Assert(request.RequestUri.AbsolutePath == "/getTaskResult" && body.RootElement.GetProperty("taskId").GetInt64() == 12345678901, "Poll does not target the original task.");
        return call == 2 ? Json("{\"errorId\":0,\"status\":\"processing\"}") : Json("{\"errorId\":0,\"status\":\"ready\",\"solution\":{\"gRecaptchaResponse\":\"mock-recaptcha-token\"}}");
    });
    var result = await Client(handler, Enabled(poll: 0)).SolveAsync(recaptcha, CancellationToken.None);
    Assert(result.Success && result.Token == "mock-recaptcha-token" && result.ErrorCode == null, "Ready token was not returned.");
    Assert(handler.Calls == 3 && timestamps.Count == 3, "Unexpected submission/poll count.");
    Assert(timestamps[1] - timestamps[0] >= TimeSpan.FromSeconds(4.9) && timestamps[2] - timestamps[1] >= TimeSpan.FromSeconds(4.9), "Polling exceeded the five-second frequency limit.");
}));
tests.Add(Test("Standalone Turnstile returns token with action/data and no challenge interception fields", async () =>
{
    var handler = new FakeHandler(async (request, call, token) =>
    {
        using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
        if (call == 1)
        {
            var task = body.RootElement.GetProperty("task");
            Assert(task.GetProperty("type").GetString() == "TurnstileTaskProxyless", "Wrong Turnstile task type.");
            Assert(task.GetProperty("action").GetString() == "signup" && task.GetProperty("data").GetString() == "test-custom-data", "Standalone widget data lost.");
            Assert(!task.TryGetProperty("pagedata", out _) && !task.TryGetProperty("userAgent", out _), "Challenge interception parameters were added.");
            return Json("{\"errorId\":0,\"taskId\":42}");
        }
        return Json("{\"errorId\":0,\"status\":\"ready\",\"solution\":{\"token\":\"mock-turnstile-token\"}}");
    });
    var result = await Client(handler, Enabled()).SolveAsync(turnstile, CancellationToken.None);
    Assert(result.Success && result.Token == "mock-turnstile-token" && handler.Calls == 2, "Turnstile ready result failed.");
}));
tests.Add(Test("Ready responses without a token fail and are not resubmitted", async () =>
{
    var handler = new FakeHandler((_, call, _) => Task.FromResult(Json(call == 1 ? "{\"errorId\":0,\"taskId\":42}" : "{\"errorId\":0,\"status\":\"ready\",\"solution\":{}}")));
    Failed(await Client(handler, Enabled()).SolveAsync(recaptcha, CancellationToken.None), "INVALID_RESPONSE"); Assert(handler.Calls == 2, "Invalid ready response created another task.");
}));
tests.Add(Test("A task-result provider error is sanitized without creating another task", async () =>
{
    var handler = new FakeHandler((_, call, _) => Task.FromResult(Json(call == 1 ? "{\"errorId\":0,\"taskId\":42}" : "{\"errorId\":12,\"errorCode\":\"ERROR_CAPTCHA_UNSOLVABLE\",\"errorDescription\":\"dummy-test-key-not-valid\"}")));
    Failed(await Client(handler, Enabled()).SolveAsync(recaptcha, CancellationToken.None), "ERROR_CAPTCHA_UNSOLVABLE"); Assert(handler.Calls == 2, "Result failure created another chargeable task.");
}));
tests.Add(Test("Perpetually processing tasks stop at the configured deadline", async () =>
{
    var handler = new FakeHandler((_, call, _) => Task.FromResult(Json(call == 1 ? "{\"errorId\":0,\"taskId\":42}" : "{\"errorId\":0,\"status\":\"processing\"}")));
    Failed(await Client(handler, Enabled(timeout: 6)).SolveAsync(recaptcha, CancellationToken.None), "TIMEOUT"); Assert(handler.Calls == 2, "Processing task exceeded its deadline or resubmitted.");
}));
await Task.WhenAll(tests);

// BrowserTests.cs is owned by the DOM automation agent and also uses fake browser/solver objects.
await CaptchaBrowserRegression.RunAsync();
if (args.Contains("--browser", StringComparer.Ordinal)) await CaptchaBrowserRegression.RunBrowserAsync();
Console.WriteLine($"{passed} client/budget tests passed; {failed} failed. No real HTTP or paid CAPTCHA requests were made.");
return failed == 0 ? 0 : 1;

sealed class FakeHandler(Func<HttpRequestMessage, int, CancellationToken, Task<HttpResponseMessage>> handle) : HttpMessageHandler
{
    private int calls;
    public int Calls => Volatile.Read(ref calls);
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => handle(request, Interlocked.Increment(ref calls), cancellationToken);
}
