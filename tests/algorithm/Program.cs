using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using MediaParser.Classes;
using MediaParser.Models.DBModels;

int passed = 0;
int failed = 0;
var web = new MediaSource { Name = "Test", SearchUrl = "https://example.test/{0}", LinkType = "User" };
var api = new MediaSource { Name = "Test API", ApiUrl = "https://example.test/{0}" };
const string originalUrl = "https://example.test/alice";

void Check(string name, Action assertion)
{
    try
    {
        assertion();
        passed++;
        Console.WriteLine($"PASS {name}");
    }
    catch (Exception ex)
    {
        failed++;
        Console.Error.WriteLine($"FAIL {name}: {ex.GetBaseException().Message}");
    }
}

void ExpectContains(string actual, string expected)
{
    if (!actual.Contains(expected, StringComparison.Ordinal))
        throw new InvalidOperationException($"Expected '{expected}', got '{actual}'.");
}

string Analyze(string body, MediaSource source, string url = originalUrl) =>
    (string)typeof(SearchAlgorithm).GetMethod("AnalyzeContent", BindingFlags.NonPublic | BindingFlags.Static)!
        .Invoke(null, new object[] { body, url, url, source })!;

bool Redirected(string from, string to, MediaSource source) =>
    (bool)typeof(SearchAlgorithm).GetMethod("IsUnrelatedRedirect", BindingFlags.NonPublic | BindingFlags.Static)!
        .Invoke(null, new object[] { from, to, source })!;

async Task<string> Response(HttpStatusCode status, string body, string contentType = "application/json")
{
    using var response = new HttpResponseMessage(status)
    {
        Content = new StringContent(body),
        RequestMessage = new HttpRequestMessage(HttpMethod.Get, originalUrl)
    };
    response.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
    var task = (Task<string>)typeof(SearchAlgorithm).GetMethod("ProcessApiResponseAsync", BindingFlags.NonPublic | BindingFlags.Static)!
        .Invoke(null, new object[] { response, originalUrl, api })!;
    return await task;
}

Check("URL escapes query identifiers and trims surrounding whitespace", () =>
    ExpectContains(SearchAlgorithm.BuildSearchUrl("https://example.test/search?keywords={0}", " Alice & Bob "), "keywords=Alice%20%26%20Bob"));
foreach (var invalid in new[] { "https\\://example.test/{0}", "file:///tmp/{0}", "https://example.test/static", "https://example.test/{1}" })
    Check($"Reject invalid URL template {invalid}", () =>
    {
        try { SearchAlgorithm.BuildSearchUrl(invalid, "alice"); }
        catch (ArgumentException) { return; }
        catch (FormatException) { return; }
        throw new InvalidOperationException("Invalid template was accepted.");
    });

Check("Profile text confirms the requested account", () =>
    ExpectContains(Analyze("<h1>alice</h1><p>42 followers</p>", web), "✅"));
Check("A generic login page does not confirm an account from its URL", () =>
    ExpectContains(Analyze("<h1>Sign in to view user profile</h1>", web), "Не вдалося підтвердити"));
Check("A page without profile evidence stays unknown", () =>
    ExpectContains(Analyze("<h1>Welcome</h1>", web), "Не вдалося підтвердити"));
Check("A matching nickname alone is not sufficient profile evidence", () =>
    ExpectContains(Analyze("<h1>alice</h1>", web), "Не вдалося підтвердити"));
Check("Script error strings do not hide a valid profile", () =>
{
    web.ErrorKeywords = new[] { "404", "User not found" };
    ExpectContains(Analyze("<script>const errors=['404', 'User not found'];</script><h1>alice</h1><p>42 followers</p>", web), "✅");
});
Check("HTML entities in visible error text are decoded", () =>
{
    web.ErrorKeywords = new[] { "This account doesn't exist" };
    ExpectContains(Analyze("<p>This account doesn&#39;t exist</p>", web), "Виявлено помилку");
});
Check("A follower count of 404 is not a page-not-found signal", () =>
{
    web.ErrorKeywords = new[] { "404" };
    ExpectContains(Analyze("<h1>alice</h1><p>404 followers</p>", web), "✅");
});
Check("Private profiles are not treated as missing", () =>
{
    web.ErrorKeywords = new[] { "This account is private" };
    ExpectContains(Analyze("<h1>alice</h1><p>This account is private</p>", web), "✅");
});
Check("An empty video feed does not prove an account is missing", () =>
{
    web.ErrorKeywords = new[] { "No videos found" };
    ExpectContains(Analyze("<h1>alice</h1><p>No videos found</p>", web), "Не вдалося підтвердити");
});
Check("An explicit page-not-found title is recognized", () =>
{
    web.ErrorKeywords = Array.Empty<string>();
    ExpectContains(Analyze("<title>Page not found</title><p>alice profile</p>", web), "404");
});
Check("A full-name result list is never reported as one existing account", () =>
{
    var fullName = new MediaSource { Name = "People", LinkType = "FullName" };
    ExpectContains(Analyze("<h1>People</h1><p>Search user profiles</p>", fullName, "https://example.test/search/results/people/?keywords=Alice%20Bob"), "Результати пошуку");
});
Check("A login redirect containing the nickname in a query is not profile evidence", () =>
{
    if (!Redirected(originalUrl, "https://example.test/login?next=/alice", web))
        throw new InvalidOperationException("Login redirect was accepted as a profile.");
});
Check("An unrelated host containing the nickname is still a redirect", () =>
{
    if (!Redirected(originalUrl, "https://alice.example.invalid/alice", web))
        throw new InvalidOperationException("Unrelated host was accepted.");
});
Check("A canonical trailing slash is not an unrelated redirect", () =>
{
    if (Redirected(originalUrl, originalUrl + "/", web))
        throw new InvalidOperationException("Canonical trailing slash was rejected.");
});
Check("A login redirect containing the nickname in its path is not profile evidence", () =>
{
    if (!Redirected(originalUrl, "https://example.test/login/alice", web))
        throw new InvalidOperationException("Login redirect was accepted as a profile.");
});

foreach (var json in new[] { "null", "[]", "true", "{", "{\"data\":null}", "{\"data\":[]}", "{\"data\":{}}", "{\"ok\":false}" })
    Check($"Unexpected/empty JSON does not confirm or invent a missing account: {json}", () =>
        ExpectContains(Analyze(json, api), "Не вдалося підтвердити"));
Check("Nullable API errors do not hide a valid account", () =>
    ExpectContains(Analyze("{\"error\":null,\"errors\":[],\"message\":null,\"username\":\"alice\"}", api), "✅"));
Check("A non-boolean ok field does not throw", () =>
    ExpectContains(Analyze("{\"ok\":\"true\",\"username\":\"alice\"}", api), "✅"));
Check("An API authentication error is not treated as an absent account", () =>
    ExpectContains(Analyze("{\"error\":\"invalid API key\"}", api), "Виявлено помилку"));
Check("An explicit API not-found message is recognized", () =>
    ExpectContains(Analyze("{\"message\":\"User not found\"}", api), "404"));

foreach (var status in new[] { HttpStatusCode.NotFound, HttpStatusCode.TooManyRequests, HttpStatusCode.Forbidden, HttpStatusCode.Unauthorized })
{
    string result = await Response(status, "<html>Gateway response</html>", "text/html");
    Check($"HTTP {(int)status} survives an HTML response body", () => ExpectContains(result, ((int)status).ToString()));
}
string badRequest = await Response(HttpStatusCode.BadRequest, "{\"description\":42}");
Check("HTTP 400 with a non-string description does not throw or become 404", () => ExpectContains(badRequest, "400"));
string jsonSuffix = await Response(HttpStatusCode.OK, "{\"username\":\"alice\"}", "application/vnd.test+json");
Check("JSON suffix content types are supported", () => ExpectContains(jsonSuffix, "✅"));

// Serve two different responses at exactly the same URL to detect stale cross-request caching.
using var listener = new TcpListener(IPAddress.Loopback, 0);
listener.Start();
int port = ((IPEndPoint)listener.LocalEndpoint).Port;
using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
var server = Task.Run(async () =>
{
    foreach (var body in new[] { "{\"message\":\"User not found\"}", "{\"username\":\"alice\"}" })
    {
        using var client = await listener.AcceptTcpClientAsync(deadline.Token);
        using var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
        while (!string.IsNullOrEmpty(await reader.ReadLineAsync(deadline.Token))) { }
        byte[] payload = Encoding.UTF8.GetBytes(body);
        string header = $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(header), deadline.Token);
        await stream.WriteAsync(payload, deadline.Token);
    }
}, deadline.Token);
string localUrl = $"http://127.0.0.1:{port}/alice";
var localApi = new MediaSource { Name = "Local API", ApiUrl = localUrl };
string first = await SearchAlgorithm.CheckAccountAsync(localUrl, null, localApi, "Default");
string second = await SearchAlgorithm.CheckAccountAsync(localUrl, null, localApi, "Default");
Check("Rechecking a URL obtains a fresh response", () =>
{
    ExpectContains(first, "404");
    ExpectContains(second, "✅");
});
await server;

if (args.Contains("--browser", StringComparer.Ordinal))
{
    using var browserListener = new TcpListener(IPAddress.Loopback, 0);
    browserListener.Start();
    int browserPort = ((IPEndPoint)browserListener.LocalEndpoint).Port;
    using var browserDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
    var connections = new List<Task>();
    var fixtureServer = Task.Run(async () =>
    {
        try
        {
            while (!browserDeadline.IsCancellationRequested)
            {
                var client = await browserListener.AcceptTcpClientAsync(browserDeadline.Token);
                connections.Add(Task.Run(async () =>
                {
                    using (client)
                    {
                        try
                        {
                            using var stream = client.GetStream();
                            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                            string requestLine = await reader.ReadLineAsync(browserDeadline.Token) ?? "";
                            if (requestLine.Length == 0) return;
                            while (!string.IsNullOrEmpty(await reader.ReadLineAsync(browserDeadline.Token))) { }
                            string html = requestLine.Contains("/missing", StringComparison.Ordinal)
                                ? "<html><title>Page not found</title><body>User not found</body></html>"
                                : requestLine.Contains("/captcha", StringComparison.Ordinal)
                                    ? "<html><title>Verification</title><body>please solve the captcha</body></html>"
                                    : "<html><title>alice profile</title><body><h1>alice</h1><p>42 followers</p></body></html>";
                            byte[] payload = Encoding.UTF8.GetBytes(html);
                            string header = $"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n\r\n";
                            await stream.WriteAsync(Encoding.ASCII.GetBytes(header), browserDeadline.Token);
                            await stream.WriteAsync(payload, browserDeadline.Token);
                        }
                        catch (OperationCanceledException) when (browserDeadline.IsCancellationRequested) { }
                        catch (IOException) { /* Chrome may close speculative connections without a request. */ }
                    }
                }));
            }
        }
        catch (OperationCanceledException) when (browserDeadline.IsCancellationRequested) { }
    }, browserDeadline.Token);
    try
    {
        using var driver = SearchAlgorithm.CreateChromeDriver("RegressionTests");
        var fixtureMedia = new MediaSource { Name = "Local fixture", SearchUrl = $"http://127.0.0.1:{browserPort}/{{0}}", LinkType = "User" };
        foreach (var (identifier, expected) in new[] { ("alice", "✅"), ("missing", "404"), ("captcha", "Потрібна капча") })
        {
            string result = await SearchAlgorithm.CheckAccountAsync($"http://127.0.0.1:{browserPort}/{identifier}", null, fixtureMedia, "RegressionTests", driver);
            Check($"Headless Chrome local fixture: {identifier}", () => ExpectContains(result, expected));
        }

        // Keep the first session alive while starting the same named profile in another session.
        // Sharing user-data-dir would make this second Chrome fail with a profile-lock error.
        using var concurrentDriver = SearchAlgorithm.CreateChromeDriver("RegressionTests");
        string concurrentResult = await SearchAlgorithm.CheckAccountAsync($"http://127.0.0.1:{browserPort}/alice", null, fixtureMedia, "RegressionTests", concurrentDriver);
        Check("Two simultaneous Chrome sessions can use the same profile name", () =>
        {
            ExpectContains(concurrentResult, "✅");
            ExpectContains(driver.Title, "Verification");
        });
    }
    catch (Exception ex)
    {
        Check("Headless Chrome starts with an isolated application profile", () => throw new InvalidOperationException(ex.GetBaseException().Message));
    }
    finally
    {
        await browserDeadline.CancelAsync();
        await fixtureServer;
        await Task.WhenAll(connections);
    }
}

Console.WriteLine($"{passed} passed; {failed} failed.");
return failed == 0 ? 0 : 1;
