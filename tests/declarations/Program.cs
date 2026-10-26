using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using MediaParser.Models;
using MediaParser.Models.DBModels;
using MediaParser.Services;
using MediaParser.Services.Captcha;
using MediaParser.Services.Declarations;
using OpenQA.Selenium;

var tests = new (string Name, Func<Task> Run)[]
{
    ("YouControl dispatch uses a typed reader without CAPTCHA services", YouControlHandoff),
    ("YouControl public metadata parser excludes private fields and preserves date-only values", YouControlReaderTests.ParserMetadata),
    ("YouControl search parser validates names, routes, empty results, and access barriers", YouControlReaderTests.SearchAndBarriers),
    ("YouControl browser reader reads public pages, disposes Chrome, and respects cancellation", YouControlReaderTests.BrowserReading),
    ("Saved public catalog import is bounded and keeps only professional metadata", YouControlReaderTests.SavedPageImport),
    ("NAZK dispatch recognizes only official full-name endpoints", SourceRecognition),
    ("NAZK searches use their dedicated client without Chrome, credentials, or CAPTCHA", DedicatedDispatch),
    ("Public API requests keep a fixed scope and escape full-name query text", FixedRequestAndEscaping),
    ("Invalid input and pre-cancelled requests do not make HTTP calls", InputValidation),
    ("HTTP restrictions and failures remain distinct from absent declarations", HttpStatuses),
    ("Malformed, deep, and unsupported response structures remain unconfirmed", InvalidResponses),
    ("Response bodies are bounded both with and without a content length", ResponseSizeBounds),
    ("Cancellation interrupts HTTP and body reads; network errors are sanitized", CancellationAndFailures),
    ("Synthetic contract: public declaration metadata is typed, bounded, and excludes private fields", SyntheticPublicMetadata),
    ("Synthetic contract: supported field aliases and empty pages stay conservative", SyntheticAliasesAndEmptyPages),
    ("Synthetic contract: UUIDs and public names are required and duplicates do not create extra records", SyntheticIdentityValidation),
    ("Synthetic contract: only the first twenty public declaration summaries are retained", SyntheticResultLimit)
};
if (args.FirstOrDefault(value => value.StartsWith("--live-youcontrol=", StringComparison.Ordinal)) is { } live)
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(125));
    var result = await new YouControlDeclarationSearch().SearchAsync(live["--live-youcontrol=".Length..], timeout.Token, args.Contains("--interactive"));
    Console.WriteLine($"Live public catalog status: {result.Status}; documents: {result.Declarations?.Count ?? 0}; {result.Reason}");
    return result.Status == AccountStatus.Found ? 0 : 2;
}
var failures = 0;
foreach (var test in tests)
{
    try { await test.Run(); Console.WriteLine($"PASS {test.Name}"); }
    catch (Exception exception) { failures++; Console.Error.WriteLine($"FAIL {test.Name}: {exception.Message}"); }
}
Console.WriteLine($"Passed {tests.Length - failures}/{tests.Length} declaration client regression tests; all HTTP responses were local fakes.");
return failures == 0 ? 0 : 1;

static Task SourceRecognition()
{
    var source = Source();
    Assert(NazkDeclarationSource.IsSource(source), "The seeded public API source was not recognized.");
    source.Name = "Custom label for official declarations";
    Assert(NazkDeclarationSource.IsSource(source), "Recognition depended on the editable display name.");
    source.ApiUrl = null;
    Assert(NazkDeclarationSource.IsSource(source), "An official public search URL was not recognized without an API template.");
    foreach (var unsafeApi in new[]
    {
        "http://public-api.nazk.gov.ua/v2/documents/list?query={0}",
        "https://public-api.nazk.gov.ua.attacker.test/v2/documents/list?query={0}",
        "https://public-api.nazk.gov.ua:444/v2/documents/list?query={0}",
        "https://user:password@public-api.nazk.gov.ua/v2/documents/list?query={0}",
        "https://public-api.nazk.gov.ua/v2/other?query={0}",
        "https://public-api.nazk.gov.ua/v2/documents/list?query={0}#fragment"
    })
    {
        source.ApiUrl = unsafeApi;
        Assert(!NazkDeclarationSource.IsSource(source), "An unsafe or different API endpoint was routed to the declaration client.");
    }
    source.ApiUrl = NazkDeclarationSource.ApiTemplate;
    source.LinkType = "User";
    Assert(!NazkDeclarationSource.IsSource(source), "A nickname source was treated as full-name declaration lookup.");
    var searchUrl = new Uri(NazkDeclarationSource.SearchUrl("Петро & Іванович"));
    Assert(searchUrl.Host == "public.nazk.gov.ua" && Uri.UnescapeDataString(searchUrl.Query).Contains("Петро & Іванович"),
        "The public registry link lost its full-name query.");
    Assert(ParseQuery(searchUrl.Query).ContainsKey("q"), "The public search link must use the form's q parameter; query belongs to the JSON API.");
    return Task.CompletedTask;
}

static async Task DedicatedDispatch()
{
    var expected = new AccountCheckResult(AccountStatus.Found, "Synthetic declaration result", Declarations: new[]
    {
        new DeclarationSummary("11111111-1111-4111-8111-111111111111", "Тестовий Користувач", "2025", null, null,
            null, null, null, "https://public.nazk.gov.ua/documents/11111111-1111-4111-8111-111111111111")
    }, DeclarationTotal: 1);
    var declarations = new FakeDeclarations(expected);
    var captcha = new ForbiddenCaptcha();
    var checker = new AccountChecker(captcha, declarations);
    var source = Source();
    source.ApiKey = "TEST-ONLY-unneeded-source-key";
    using var cancellation = new CancellationTokenSource();
    const string fullName = "Тестовий Користувач";
    Assert(await checker.CheckAsync(source, fullName, cancellation.Token) == expected, "Normal checks did not dispatch to the typed public declaration client.");
    var budget = new CaptchaSolveBudget(2);
    Assert(await checker.CheckWithCaptchaAsync(source, fullName, budget, _ => throw new Exception("Unexpected CAPTCHA progress"), cancellation.Token) == expected,
        "A CAPTCHA-enabled search did not retain dedicated declaration dispatch.");
    Assert(await checker.CheckInteractiveAsync(source, fullName, cancellation.Token) == expected,
        "An official declaration source fell through to interactive Chrome.");
    Assert(declarations.Calls == 3 && declarations.LastName == fullName && declarations.LastToken == cancellation.Token &&
        captcha.Calls == 0 && budget.Used == 0, "Declaration dispatch touched CAPTCHA, changed the query, or dropped cancellation.");
    Assert((await new AccountChecker().CheckAsync(source, fullName, cancellation.Token)).Status == AccountStatus.Unconfirmed,
        "A missing dedicated declaration client fell back to a browser or invented an absent person.");
}

static async Task FixedRequestAndEscaping()
{
    using var handler = new FakeHandler((_, _) => Task.FromResult(Response(HttpStatusCode.Forbidden, "private response text")));
    using var client = new HttpClient(handler) { BaseAddress = new Uri("https://attacker.invalid/") };
    var result = await new NazkDeclarationSearch(client).SearchAsync("  Петренко\t Петро&full_search=1&page=999  ", CancellationToken.None);
    var uri = handler.LastUri ?? throw new InvalidOperationException("The public API request was not sent to the fake handler.");
    var query = ParseQuery(uri.Query);
    Assert(uri.Scheme == "https" && uri.Host == "public-api.nazk.gov.ua" && uri.AbsolutePath == "/v2/documents/list" &&
        query.Count == 3 && query["query"] == "Петренко Петро&full_search=1&page=999" && query["full_search"] == "0" && query["page"] == "1",
        "Full-name input changed the fixed host, route, first page, or name-only search scope.");
    Assert(handler.Calls == 1 && handler.Authorization == null && !handler.HasCookies && handler.AcceptsJson &&
        result.Status == AccountStatus.Blocked, "Public search forwarded credentials or used unexpected requests.");
}

static async Task InputValidation()
{
    using var handler = new FakeHandler((_, _) => throw new Exception("Invalid input must not send HTTP."));
    using var client = new HttpClient(handler);
    var search = new NazkDeclarationSearch(client);
    foreach (var value in new[] { "", "  ", "ab", new string('а', 101), "Тест\u0001ПІБ" })
        Assert((await search.SearchAsync(value, CancellationToken.None)).Status == AccountStatus.Unconfirmed,
            "Malformed full-name input was not rejected conservatively.");
    using var cancellation = new CancellationTokenSource();
    cancellation.Cancel();
    Assert((await search.SearchAsync("Тестовий Користувач", cancellation.Token)).Status == AccountStatus.Cancelled && handler.Calls == 0,
        "A pre-cancelled or malformed request made an HTTP call.");
}

static async Task HttpStatuses()
{
    foreach (var status in new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden, HttpStatusCode.TooManyRequests,
        HttpStatusCode.BadRequest, HttpStatusCode.NotFound, HttpStatusCode.InternalServerError, HttpStatusCode.BadGateway })
    {
        const string privateText = "TEST-ONLY-private-upstream-error-952640";
        using var handler = new FakeHandler((_, _) => Task.FromResult(Response(status, privateText)));
        using var client = new HttpClient(handler);
        var result = await new NazkDeclarationSearch(client).SearchAsync("Тестовий Користувач", CancellationToken.None);
        var expected = status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests
            ? AccountStatus.Blocked : AccountStatus.Error;
        Assert(result.Status == expected && result.Declarations == null && !JsonSerializer.Serialize(result).Contains(privateText),
            "An HTTP failure invented absent declarations or exposed an upstream error body.");
    }
}

static async Task InvalidResponses()
{
    foreach (var body in new[] { "<html>Gateway</html>", "{", "null", "[]", "{}", "{\"unexpected\":true}",
        "{\"error\":\"TEST-ONLY-upstream-secret\"}", new string('[', 40) + "0" + new string(']', 40) })
    {
        using var handler = new FakeHandler((_, _) => Task.FromResult(Response(HttpStatusCode.OK, body)));
        using var client = new HttpClient(handler);
        var result = await new NazkDeclarationSearch(client).SearchAsync("Тестовий Користувач", CancellationToken.None);
        Assert(result.Status == AccountStatus.Unconfirmed && result.Declarations == null &&
            !JsonSerializer.Serialize(result).Contains("TEST-ONLY-upstream-secret"),
            "An invalid or changed schema confirmed an account, invented zero declarations, or leaked raw API data.");
    }
}

static async Task ResponseSizeBounds()
{
    const int maximum = 2 * 1024 * 1024;
    var shortBody = new CountingReadStream(Encoding.UTF8.GetBytes("{}"));
    using (var handler = new FakeHandler((_, _) =>
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(shortBody) };
        response.Content.Headers.ContentLength = maximum + 1;
        return Task.FromResult(response);
    }))
    using (var client = new HttpClient(handler))
    {
        var result = await new NazkDeclarationSearch(client).SearchAsync("Тестовий Користувач", CancellationToken.None);
        Assert(result.Status == AccountStatus.Unconfirmed && shortBody.BytesRead == 0,
            "An oversized declared response was read or accepted.");
    }
    var streamedBody = new CountingReadStream(Enumerable.Repeat((byte)' ', maximum + 1).ToArray());
    using (var handler = new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
    { Content = new StreamContent(streamedBody) })))
    using (var client = new HttpClient(handler))
    {
        var result = await new NazkDeclarationSearch(client).SearchAsync("Тестовий Користувач", CancellationToken.None);
        Assert(result.Status == AccountStatus.Unconfirmed && streamedBody.BytesRead is > 0 and <= maximum + 16384,
            "A response without Content-Length bypassed the streaming size bound.");
    }
}

static async Task CancellationAndFailures()
{
    var enteredHttp = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    using (var cancellation = new CancellationTokenSource())
    using (var handler = new FakeHandler(async (_, token) =>
    {
        enteredHttp.TrySetResult();
        await Task.Delay(Timeout.InfiniteTimeSpan, token);
        return Response(HttpStatusCode.OK, "{}");
    }))
    using (var client = new HttpClient(handler))
    {
        var pending = new NazkDeclarationSearch(client).SearchAsync("Тестовий Користувач", cancellation.Token);
        await enteredHttp.Task.WaitAsync(TimeSpan.FromSeconds(3));
        cancellation.Cancel();
        Assert((await pending.WaitAsync(TimeSpan.FromSeconds(3))).Status == AccountStatus.Cancelled,
            "Cancellation did not interrupt the active HTTP request.");
    }
    var body = new BlockingReadStream();
    using (var cancellation = new CancellationTokenSource())
    using (var handler = new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) })))
    using (var client = new HttpClient(handler))
    {
        var pending = new NazkDeclarationSearch(client).SearchAsync("Тестовий Користувач", cancellation.Token);
        await body.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        cancellation.Cancel();
        Assert((await pending.WaitAsync(TimeSpan.FromSeconds(3))).Status == AccountStatus.Cancelled,
            "Cancellation did not interrupt response-body reads.");
    }
    foreach (var exception in new Exception[] { new HttpRequestException("TEST-ONLY-network-secret"), new IOException("TEST-ONLY-io-secret"), new OperationCanceledException() })
    {
        using var handler = new FakeHandler((_, _) => Task.FromException<HttpResponseMessage>(exception));
        using var client = new HttpClient(handler);
        var result = await new NazkDeclarationSearch(client).SearchAsync("Тестовий Користувач", CancellationToken.None);
        Assert(result.Status == AccountStatus.Error && !JsonSerializer.Serialize(result).Contains("TEST-ONLY"),
            "An internal timeout or network failure was mislabeled or exposed its exception text.");
    }
}

// These payloads exercise the explicitly supported parser contract. They are synthetic,
// not captured NAZK responses; access to the live API was unavailable during implementation.
static async Task SyntheticPublicMetadata()
{
    var firstId = "AAAAAAAA-AAAA-4AAA-8AAA-AAAAAAAAAAAA";
    var secondId = "22222222-2222-4222-8222-222222222222";
    var submitted = new DateTimeOffset(2026, 3, 1, 10, 20, 30, TimeSpan.Zero);
    var payload = new
    {
        error = false,
        total = 25,
        data = new object[]
        {
            new
            {
                id = firstId, declaration_year = 2025, declaration_type = 1, document_type = 3, date = submitted.ToUnixTimeSeconds(),
                infocard = new
                {
                    last_name = "  Тестовий  ", first_name = "Користувач", middle_name = "Петрович",
                    position = "Посада & <тест>", work_place = "Тестова установа",
                    tax_id = "PRIVATE-TAX-ID-SENTINEL", email = "PRIVATE-EMAIL-SENTINEL", address = "PRIVATE-ADDRESS-SENTINEL"
                },
                url = "https://attacker.invalid/PRIVATE-URL-SENTINEL", assets = new[] { "PRIVATE-ASSETS-SENTINEL" }
            },
            new
            {
                id = secondId, fullName = "Інший Тестовий Користувач", declaration_year = "2024",
                declaration_type = "Кандидата на посаду", document_type = "Декларація", date = "2026-03-02T12:34:56+02:00",
                post = "Аналітик", workPlace = "Інша установа", hidden = "PRIVATE-OTHER-SENTINEL"
            }
        }
    };
    var result = await SearchFixture(JsonSerializer.Serialize(payload));
    Assert(result.Status == AccountStatus.Found && result.DeclarationTotal == 25 && result.Declarations?.Count == 2,
        "The synthetic metadata page was not parsed as two public document summaries.");
    var first = result.Declarations![0];
    Assert(first.Id == firstId.ToLowerInvariant() && first.FullName == "Тестовий Користувач Петрович" &&
        first.DeclarationYear == "2025" && first.DeclarationType == "Щорічна" && first.DocumentType == "Виправлена декларація" &&
        first.Position == "Посада & <тест>" && first.Workplace == "Тестова установа" && first.SubmittedAt == submitted &&
        first.Url == "https://public.nazk.gov.ua/documents/" + firstId.ToLowerInvariant(),
        "Public name, year, type labels, position, employer, Unix submission time, or canonical document URL changed.");
    var second = result.Declarations[1];
    Assert(second.DeclarationYear == "2024" && second.DeclarationType == "Кандидата на посаду" && second.DocumentType == "Декларація" &&
        second.Position == "Аналітик" && second.Workplace == "Інша установа" &&
        second.SubmittedAt == new DateTimeOffset(2026, 3, 2, 10, 34, 56, TimeSpan.Zero) && second.SubmittedAt?.Offset == TimeSpan.Zero,
        "String fields, alias fields, or offset submission timestamps were not preserved and normalized to UTC.");
    Assert(!JsonSerializer.Serialize(result).Contains("PRIVATE-", StringComparison.Ordinal),
        "Private or unexpected upstream fields were copied into the public declaration result.");
}

static async Task SyntheticAliasesAndEmptyPages()
{
    const string id = "11111111-1111-4111-8111-111111111111";
    foreach (var arrayName in new[] { "data", "items" })
    foreach (var totalName in new[] { "total", "totalItems", "count" })
    {
        var root = new Dictionary<string, object>
        {
            [arrayName] = new[] { new { id, infocard = new { fullname = "Тестовий Користувач", declarationYear = "2025", declarationType = "1", documentType = "1", placeOfWork = "Тестова установа" } } },
            [totalName] = "1"
        };
        var result = await SearchFixture(JsonSerializer.Serialize(root));
        Assert(result.Status == AccountStatus.Found && result.DeclarationTotal == 1 &&
            result.Declarations?.Single() is { FullName: "Тестовий Користувач", DeclarationYear: "2025", DeclarationType: "Щорічна", DocumentType: "Декларація", Workplace: "Тестова установа" },
            "A supported page-count, array, or infocard field alias was lost.");
    }
    foreach (var card in new object[]
    {
        new { lastName = "Тестовий", firstName = "Користувач", middleName = "Петрович" },
        new { lastname = "Тестовий", firstname = "Користувач", middlename = "Петрович" }
    })
    {
        var result = await SearchFixture(JsonSerializer.Serialize(new { data = new[] { new { id, infocard = card } } }));
        Assert(result.Declarations?.Single().FullName == "Тестовий Користувач Петрович",
            "Camel-case or lowercase public name components were not combined.");
    }
    var empty = await SearchFixture("{\"data\":[],\"total\":0}");
    Assert(empty.Status == AccountStatus.Unconfirmed && empty.Declarations?.Count == 0 && empty.DeclarationTotal == 0,
        "An empty public search page claimed that a person has no declarations instead of reporting an unconfirmed empty search.");
    var inconsistent = await SearchFixture("{\"items\":[],\"totalItems\":3}");
    Assert(inconsistent.Status == AccountStatus.Unconfirmed && inconsistent.Declarations == null,
        "An inconsistent empty page with a nonzero total was presented as a confirmed empty search.");
}

static async Task SyntheticIdentityValidation()
{
    const string id = "11111111-1111-4111-8111-111111111111";
    foreach (var body in new[]
    {
        "{\"data\":[null,42,{}, {\"id\":\"invalid\",\"full_name\":\"Тестовий Користувач\"}]}",
        "{\"data\":[{\"id\":\"00000000-0000-0000-0000-000000000000\",\"full_name\":\"Тестовий Користувач\"}]}",
        $"{{\"data\":[{{\"id\":\"{id}\"}}]}}",
        $"{{\"data\":[{{\"id\":\"{id}\",\"full_name\":null}}]}}",
        $"{{\"data\":[{{\"id\":\"{id}\",\"full_name\":\"  \"}}]}}",
        $"{{\"data\":[{{\"id\":\"{id}\",\"full_name\":123}}]}}",
        $"{{\"data\":[{{\"id\":\"{id}\",\"full_name\":true}}]}}"
    })
    {
        var result = await SearchFixture(body);
        Assert(result.Status == AccountStatus.Unconfirmed && result.Declarations == null,
            "A document without a valid UUID and public text name was confirmed.");
    }
    var duplicate = await SearchFixture(JsonSerializer.Serialize(new
    {
        data = new[]
        {
            new { id, full_name = "Перший Тестовий Користувач" },
            new { id, full_name = "Дубль Тестового Користувача" }
        }, total = 2
    }));
    Assert(duplicate.Status == AccountStatus.Found && duplicate.Declarations?.Count == 1 &&
        duplicate.Declarations[0].FullName == "Перший Тестовий Користувач" && duplicate.DeclarationTotal == 2,
        "Duplicate document UUIDs created repeated summaries or lost the provider total.");
    var unusableOptionalFields = await SearchFixture(JsonSerializer.Serialize(new
    {
        data = new[] { new { id, full_name = "Тестовий Користувач", declaration_type = 999, document_type = 999,
            date = "2010-01-01T00:00:00Z", infocard = new { full_name = "Тестовий Користувач", position = new string('x', 501), work_place = "Установа\u0001" } } }
    }));
    Assert(unusableOptionalFields.Declarations?.Single() is { DeclarationType: null, DocumentType: null, SubmittedAt: null, Position: null, Workplace: null },
        "Unknown numeric types, invalid dates, or overlong/control-character metadata were fabricated or copied.");
}

static async Task SyntheticResultLimit()
{
    var data = Enumerable.Range(1, 25).Select(index => new
    {
        id = $"00000000-0000-4000-8000-{index:000000000000}", full_name = $"Синтетичний Користувач {index}", declaration_year = "2025"
    }).ToArray();
    var result = await SearchFixture(JsonSerializer.Serialize(new { data, total = 125 }));
    Assert(result.Status == AccountStatus.Found && result.Declarations?.Count == 20 && result.DeclarationTotal == 125 &&
        result.Declarations[0].FullName == "Синтетичний Користувач 1" && result.Declarations[^1].FullName == "Синтетичний Користувач 20",
        "The first-page result cap or upstream total count was not preserved.");
}

static async Task<AccountCheckResult> SearchFixture(string payload)
{
    using var handler = new FakeHandler((_, _) => Task.FromResult(Response(HttpStatusCode.OK, payload)));
    using var client = new HttpClient(handler);
    return await new NazkDeclarationSearch(client).SearchAsync("Тестовий Користувач", CancellationToken.None);
}

static async Task YouControlHandoff()
{
    var source = new MediaSource { Name = YouControlDeclarationSource.Name, LinkType = "FullName",
        SearchUrl = YouControlDeclarationSource.SearchTemplate, ApiKey = "TEST-ONLY-unused-key" };
    Assert(YouControlDeclarationSource.IsSource(source), "The default YouControl source was not recognized.");
    source.Name = "Renamed catalog";
    Assert(YouControlDeclarationSource.IsSource(source), "Changing the display name broke the public search handoff.");
    foreach (var invalid in new[] { "http://youcontrol.com.ua/catalog/individuals/search/?q={0}",
        "https://youcontrol.com.ua.attacker.test/catalog/individuals/search/?q={0}",
        "https://youcontrol.com.ua:444/catalog/individuals/search/?q={0}",
        "https://user:password@youcontrol.com.ua/catalog/individuals/search/?q={0}",
        "https://youcontrol.com.ua/catalog/individuals/?q={0}",
        "https://youcontrol.com.ua/catalog/individuals/search/?q={0}#fragment" })
    {
        source.SearchUrl = invalid;
        Assert(!YouControlDeclarationSource.IsSource(source), "An unsafe or different endpoint was recognized as the YouControl catalog search.");
    }
    source.SearchUrl = YouControlDeclarationSource.SearchTemplate;
    source.LinkType = "User";
    Assert(!YouControlDeclarationSource.IsSource(source), "The declaration catalog accepted nickname mode.");
    source.LinkType = "FullName";
    source.ApiUrl = "https://custom.example/{0}";
    Assert(!YouControlDeclarationSource.IsSource(source), "A custom API source was overridden by the handoff.");
    source.ApiUrl = null;
    var query = "Тестовий & Інший?q=wrong#fragment";
    var uri = new Uri(YouControlDeclarationSource.SearchUrl(query));
    var parsed = ParseQuery(uri.Query);
    Assert(uri.Host == "youcontrol.com.ua" && uri.AbsolutePath == "/catalog/individuals/search/" &&
        parsed.Count == 1 && parsed["q"] == query && uri.Fragment.Length == 0, "Input changed the public search scope.");
    var unusedDeclarations = new FakeDeclarations(new(AccountStatus.Error, "Must not be used"));
    var captcha = new ForbiddenCaptcha();
    var reader = new TestYouControlReader();
    var checker = new AccountChecker(captcha, unusedDeclarations, reader);
    var budget = new CaptchaSolveBudget(2);
    var results = new[] { await checker.CheckAsync(source, query, CancellationToken.None),
        await checker.CheckWithCaptchaAsync(source, query, budget, _ => throw new Exception("Unexpected CAPTCHA progress"), CancellationToken.None),
        await checker.CheckInteractiveAsync(source, query, CancellationToken.None) };
    Assert(results.All(result => result == reader.Result) && reader.Modes.SequenceEqual(new[] { false, false, true }),
        "Normal and manual checks did not dispatch to the typed catalog reader.");
    Assert(unusedDeclarations.Calls == 0 && captcha.Calls == 0 && budget.Used == 0, "The handoff touched API or CAPTCHA services.");
    using var cancellation = new CancellationTokenSource();
    cancellation.Cancel();
    Assert((await checker.CheckAsync(source, query, cancellation.Token)).Status == AccountStatus.Cancelled,
        "Cancelled handoff continued preparing a result.");
}

static MediaSource Source() => new()
{
    Id = 20, Name = NazkDeclarationSource.Name, LinkType = "FullName", ApiUrl = NazkDeclarationSource.ApiTemplate,
    SearchUrl = NazkDeclarationSource.SearchTemplate, ErrorKeywordsRaw = "[]"
};
static HttpResponseMessage Response(HttpStatusCode status, string body) => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
static Dictionary<string, string> ParseQuery(string query) => query.TrimStart('?').Split('&').Select(part => part.Split('=', 2))
    .ToDictionary(parts => WebUtility.UrlDecode(parts[0]), parts => parts.Length > 1 ? WebUtility.UrlDecode(parts[1]) : "");
static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
    public int Calls { get; private set; }
    public Uri? LastUri { get; private set; }
    public AuthenticationHeaderValue? Authorization { get; private set; }
    public bool HasCookies { get; private set; }
    public bool AcceptsJson { get; private set; }
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Calls++; LastUri = request.RequestUri; Authorization = request.Headers.Authorization;
        HasCookies = request.Headers.Contains("Cookie");
        AcceptsJson = request.Headers.Accept.Any(value => value.MediaType == "application/json");
        return respond(request, cancellationToken);
    }
}

sealed class FakeDeclarations(AccountCheckResult result) : INazkDeclarationSearch
{
    public int Calls { get; private set; }
    public string? LastName { get; private set; }
    public CancellationToken LastToken { get; private set; }
    public Task<AccountCheckResult> SearchAsync(string fullName, CancellationToken cancellationToken)
    { Calls++; LastName = fullName; LastToken = cancellationToken; return Task.FromResult(result); }
}

sealed class ForbiddenCaptcha : ICaptchaAutomation
{
    public int Calls { get; private set; }
    public Task<CaptchaAttemptResult> TrySolveAsync(IWebDriver driver, CaptchaSolveBudget budget,
        CancellationToken cancellationToken, Action<string>? onProgress = null)
    { Calls++; throw new InvalidOperationException("Declaration lookups must not run CAPTCHA automation."); }
}

sealed class CountingReadStream(byte[] payload) : Stream
{
    private readonly MemoryStream _inner = new(payload);
    public long BytesRead { get; private set; }
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) { var read = _inner.Read(buffer, offset, count); BytesRead += read; return read; }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    { var read = await _inner.ReadAsync(buffer, cancellationToken); BytesRead += read; return read; }
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    protected override void Dispose(bool disposing) { if (disposing) _inner.Dispose(); base.Dispose(disposing); }
}

sealed class BlockingReadStream : Stream
{
    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    { Entered.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); return 0; }
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

sealed class TestYouControlReader : IYouControlDeclarationSearch
{
    public List<bool> Modes { get; } = new();
    public AccountCheckResult Result { get; } = new(AccountStatus.Found, "Typed public metadata", Declarations: new[]
    { new DeclarationSummary("11111111-1111-4111-8111-111111111111", "Тестовий Користувач", null, "Річна", null,
        "Фахівець", "Тестова установа", null, "https://youcontrol.com.ua/catalog/individuals/declaration/11111111-1111-4111-8111-111111111111/", "2026-03-24") }, DeclarationTotal: 1);
    public Task<AccountCheckResult> SearchAsync(string fullName, CancellationToken cancellationToken, bool interactive = false)
    { Modes.Add(interactive); return Task.FromResult(cancellationToken.IsCancellationRequested ? new(AccountStatus.Cancelled, "Cancelled") : Result); }
}
