using System.Diagnostics;
using System.Net;
using System.Text.Json;
using MediaParser.Classes;
using MediaParser.Models;
using MediaParser.Models.DBModels;
using OpenQA.Selenium;

int passed = 0, failed = 0;
var source = new MediaSource { Name = "Fixture", LinkType = "User" };
void Check(string name, Action assertion)
{
    try { assertion(); passed++; Console.WriteLine($"PASS {name}"); }
    catch (Exception ex) { failed++; Console.Error.WriteLine($"FAIL {name}: {ex.GetBaseException().Message}"); }
}
void Status(AccountCheckResult actual, AccountStatus expected)
{
    if (actual.Status != expected) throw new InvalidOperationException($"Expected {expected}; got {actual.Status}: {actual.Reason}");
    if (string.IsNullOrWhiteSpace(actual.Reason)) throw new InvalidOperationException("A readable reason is required.");
}
void Html(string name, string url, string body, AccountStatus expected, string? final = null, MediaSource? media = null) =>
    Check(name, () => Status(SiteRules.EvaluateHtml(body, url, final ?? url, media ?? source), expected));
string Meta(string key, string value) => $"<meta property=\"{key}\" content=\"{WebUtility.HtmlEncode(value)}\">";
string ProfileMeta(string url, string username) => Meta("og:url", url) + Meta("og:type", "profile") + Meta("profile:username", username);
string Instagram(string username = "alice") => Meta("og:url", $"https://www.instagram.com/{username}/") +
    Meta("instapp:owner_user_id", "12345") + Meta("og:title", $"Аліса (@{username}) • Фото та відео");
const string github = "https://github.com/alice";
const string instagram = "https://www.instagram.com/alice/";

Check("URL identifiers are escaped and trimmed", () =>
{
    string actual = SearchAlgorithm.BuildSearchUrl("https://example.test/search?keywords={0}", " Alice & Bob ");
    if (!actual.EndsWith("keywords=Alice%20%26%20Bob", StringComparison.Ordinal)) throw new InvalidOperationException(actual);
});
foreach (var template in new[] { "https\\://example.test/{0}", "file:///tmp/{0}", "https://example.test/static", "https://example.test/{1}" })
    Check($"Reject malformed/unsafe URL template {template}", () =>
    {
        try { SearchAlgorithm.BuildSearchUrl(template, "alice"); }
        catch (ArgumentException) { return; }
        catch (FormatException) { return; }
        throw new InvalidOperationException("Invalid URL template accepted.");
    });

Html("Generic words and an exact nickname never prove a custom account", "https://example.test/alice", "<h1>alice</h1><p>profile followers bio user account avatar</p>", AccountStatus.Unconfirmed);
Html("Custom domain cannot impersonate Instagram by its source name or metadata", "https://example.test/alice", Instagram(), AccountStatus.Unconfirmed,
    media: new MediaSource { Name = "Instagram" });
Html("Instagram owner identity and localized title confirm the exact account", instagram, Instagram(), AccountStatus.Found);
string modernInstagram = Meta("og:url", instagram) + Meta("og:type", "profile") + Meta("og:title", "Аліса (@alice) • Світлини та відео");
Html("Current Instagram iOS app link confirms the exact public username", instagram,
    modernInstagram + Meta("al:ios:url", "instagram://user?username=alice"), AccountStatus.Found);
Html("Current Instagram Android app link confirms the exact public username", instagram,
    modernInstagram + Meta("al:android:url", "https://instagram.com/_u/alice/"), AccountStatus.Found);
Html("Instagram app link for another username does not confirm a profile", instagram,
    modernInstagram + Meta("al:ios:url", "instagram://user?username=alice_two"), AccountStatus.Unconfirmed);
Html("Instagram app link on another domain is not provider evidence", instagram,
    modernInstagram + Meta("al:android:url", "https://instagram.com.example.test/_u/alice/"), AccountStatus.Unconfirmed);
Html("Instagram title and requested URL without independent identity evidence remain unconfirmed", instagram,
    modernInstagram, AccountStatus.Unconfirmed);
Html("A private Instagram profile with exact metadata remains a real account", instagram, Instagram() + "<p>This account is private</p>", AccountStatus.Found);
Html("Instagram wrong username metadata is not a match", instagram, Instagram("alice2"), AccountStatus.Unconfirmed);
Html("Instagram title substring is not exact username proof", instagram,
    Meta("og:url", instagram) + Meta("instapp:owner_user_id", "123") + Meta("og:title", "Someone (@alice_two)"), AccountStatus.Unconfirmed);
Html("Username metadata with a wrong canonical URL is not a match", instagram,
    ProfileMeta("https://www.instagram.com/bob/", "alice"), AccountStatus.Unconfirmed);
Html("Canonical URL alone is not account evidence", instagram, Meta("og:url", instagram), AccountStatus.Unconfirmed);
Html("Profile words alone on a known provider remain unknown", instagram, "<h1>alice</h1><p>42 followers; bio and profile</p>", AccountStatus.Unconfirmed);
Html("Visible login form overrides positive profile metadata", instagram, Instagram() + "<form><input type=password></form>", AccountStatus.Blocked);
Html("Hidden login form does not override public profile evidence", instagram, Instagram() + "<form style='display: none'><input type=password></form>", AccountStatus.Found);
Html("A login redirect is blocked even with echoed profile metadata", instagram, Instagram(), AccountStatus.Blocked, "https://www.instagram.com/accounts/login/?next=/alice/");
Html("A login page retaining the requested URL is blocked", instagram, Instagram() + "<h1>Log in to continue</h1>", AccountStatus.Blocked);
Html("CAPTCHA requests are blocked and not bypassed", instagram, Instagram() + "<h1>Human verification</h1>", AccountStatus.Blocked);
Html("CAPTCHA phrases in script bundles do not block a public profile", instagram, Instagram() + "<script>const x='human verification';</script>", AccountStatus.Found);
Html("Rate limit is not an absent account", github, "<h1>Too many requests</h1>", AccountStatus.Blocked);
Html("Ukrainian login barrier is recognized", github, "<h1>Увійдіть, щоб продовжити</h1>", AccountStatus.Blocked);
Html("Unrelated redirect cannot match a nickname substring", github, "<h1>alice</h1>", AccountStatus.Unconfirmed, "https://alice.example.invalid/alice");
Html("A canonical trailing slash remains the same profile", instagram, Instagram(), AccountStatus.Found, instagram.TrimEnd('/'));
Html("Explicit not-found heading is negative evidence", github, "<h1>User not found</h1>", AccountStatus.NotFound);
Html("A localized not-found page is recognized", github, "<title>Сторінку не знайдено</title>", AccountStatus.NotFound);
Html("A biography mentioning a missing user is not negative evidence", instagram, Instagram() + "<p>User not found is my favourite phrase.</p>", AccountStatus.Found);
Html("A follower count of 404 is harmless", instagram, Instagram() + "<p>404 followers</p>", AccountStatus.Found);
Html("No videos found does not mean no account", "https://www.tiktok.com/@alice", "<h1>alice</h1><p>No videos found</p>", AccountStatus.Unconfirmed);
Html("Contradictory profile and not-found evidence stays unknown", instagram, Instagram() + "<h1>User not found</h1>", AccountStatus.Unconfirmed);
Html("Full-name results are never one confirmed account", "https://example.test/search?keywords=Alice", "<h1>Alice people profiles</h1>", AccountStatus.Unconfirmed,
    media: new MediaSource { Name = "People", LinkType = "FullName" });

string snap = Meta("og:url", "https://www.snapchat.com/add/alice") + Meta("og:title", "Аліса (@alice) • Snapchat") + Meta("twitter:app:url:iphone", "snapchat://add/alice");
Html("Snapchat exact canonical, handle and app link agree", "https://www.snapchat.com/add/alice", snap, AccountStatus.Found);
Html("Snapchat generic contact template cannot prove an account", "https://www.snapchat.com/add/alice", Meta("og:url", "https://www.snapchat.com/add/alice") + "<h1>alice profile</h1>", AccountStatus.Unconfirmed);
string gh = Meta("og:url", github) + Meta("octolytics-dimension-user_login", "alice") + Meta("octolytics-dimension-user_id", "9876");
Html("GitHub viewed-user metadata agrees with the profile URL", github, gh, AccountStatus.Found);
Html("GitHub invalid provider id does not prove an account", github, gh.Replace("9876", "0", StringComparison.Ordinal), AccountStatus.Unconfirmed);
string telegram = Meta("og:url", "https://t.me/alice") + "<div class=tgme_page_title>Аліса</div><div class=tgme_page_extra>@alice</div><img class=tgme_page_photo_image>";
Html("Telegram exact public-profile markers confirm the username", "https://t.me/alice", telegram, AccountStatus.Found);
Html("Telegram contact-any-username template stays unknown", "https://t.me/alice", Meta("og:url", "https://t.me/alice") + "<div class=tgme_page_title>@alice</div><p>You can contact @alice right away.</p>", AccountStatus.Unconfirmed);
string steam = Meta("og:url", "https://steamcommunity.com/id/alice") + "<div class=profile_header><span class=actual_persona_name>Інше публічне ім’я</span><a class=playerAvatar></a></div>";
Html("Steam profile DOM and exact vanity URL agree", "https://steamcommunity.com/id/alice", steam, AccountStatus.Found);
Html("Steam explicit missing-profile error is recognized", "https://steamcommunity.com/id/alice", "<div class=error_ctn>The specified profile could not be found.</div>", AccountStatus.NotFound);
string yt = Meta("og:url", "https://www.youtube.com/channel/UCabcdefghijk123") + "<script>var ytInitialData = {\"metadata\":{\"channelMetadataRenderer\":{\"title\":\"日本語\",\"externalId\":\"UCabcdefghijk123\",\"vanityChannelUrl\":\"https://www.youtube.com/@alice\"}}};</script>";
Html("YouTube exact handle and channel id agree across metadata", "https://www.youtube.com/@alice", yt, AccountStatus.Found);
Html("YouTube another handle is not a match", "https://www.youtube.com/@bob", yt, AccountStatus.Unconfirmed);
string schema = JsonSerializer.Serialize(new Dictionary<string, object>
{
    ["@context"] = "https://schema.org", ["@type"] = "ProfilePage", ["url"] = instagram,
    ["mainEntity"] = new Dictionary<string, object> { ["@type"] = "Person", ["name"] = "日本語", ["alternateName"] = "@alice", ["url"] = instagram }
});
Html("Localized JSON-LD ProfilePage has exact identity and profile URL", instagram, $"<script type='application/ld+json'>{schema}</script>", AccountStatus.Found);
Html("Article author schema is not the identity of a requested profile", instagram,
    $"<script type='application/ld+json'>{{\"@type\":\"Article\",\"author\":{{\"@type\":\"Person\",\"alternateName\":\"alice\",\"url\":\"{instagram}\"}}}}</script>", AccountStatus.Unconfirmed);

foreach (var url in new[]
{
    "https://www.facebook.com/alice", "https://www.pinterest.com/alice/", "https://www.linkedin.com/in/alice/",
    "https://www.twitch.tv/alice", "https://soundcloud.com/alice", "https://rumble.com/alice",
    "https://medium.com/@alice", "https://open.spotify.com/user/alice"
})
{
    Html($"Known provider validates exact standard profile metadata: {url}", url, ProfileMeta(url, "alice"), AccountStatus.Found);
    Html($"Known provider rejects a different profile identity: {url}", url, ProfileMeta(url, "someone_else"), AccountStatus.Unconfirmed);
    Html($"Known provider does not trust its own URL alone: {url}", url, Meta("og:url", url) + "<h1>alice</h1><p>profile followers</p>", AccountStatus.Unconfirmed);
}
Html("A LinkedIn people search cannot masquerade as one profile", "https://www.linkedin.com/search/results/people/?keywords=alice",
    ProfileMeta("https://www.linkedin.com/search/results/people/?keywords=alice", "alice"), AccountStatus.Unconfirmed);
Html("Rumble user account has exact user-namespace metadata", "https://rumble.com/user/alice",
    ProfileMeta("https://rumble.com/user/alice", "alice"), AccountStatus.Found);
Html("Rumble root channel alias can resolve to its channel path", "https://rumble.com/alice",
    ProfileMeta("https://rumble.com/c/alice", "alice"), AccountStatus.Found, "https://rumble.com/c/alice");
Html("Rumble channel redirect cannot silently become a user account", "https://rumble.com/alice",
    ProfileMeta("https://rumble.com/user/alice", "alice"), AccountStatus.Unconfirmed, "https://rumble.com/user/alice");
Html("Rumble same spelling in channel metadata does not confirm a user", "https://rumble.com/user/alice",
    ProfileMeta("https://rumble.com/c/alice", "alice"), AccountStatus.Unconfirmed);
Html("Rumble same spelling in user metadata does not confirm a channel", "https://rumble.com/c/alice",
    ProfileMeta("https://rumble.com/user/alice", "alice"), AccountStatus.Unconfirmed);
Html("Rumble JSON-LD cannot cross from user account to channel namespace", "https://rumble.com/user/alice",
    "<script type='application/ld+json'>{\"@type\":\"Person\",\"alternateName\":\"alice\",\"url\":\"https://rumble.com/c/alice\"}</script>", AccountStatus.Unconfirmed);
Html("Epic authentication namespace is not a public username-profile route", "https://www.epicgames.com/id/alice",
    ProfileMeta("https://www.epicgames.com/id/alice", "alice"), AccountStatus.Unconfirmed);
Html("An Epic id-path 404 does not prove that a display name is absent", "https://www.epicgames.com/id/alice",
    "<title>Page not found</title>", AccountStatus.Unconfirmed);
Html("Epic login still reports the real access barrier", "https://www.epicgames.com/id/alice",
    "<h1>Log in</h1>", AccountStatus.Blocked, "https://www.epicgames.com/id/login");
foreach (var url in new[] { "https://www.epicgames.com/id/{0}", "https://open.spotify.com/user/{0}", "https://steamcommunity.com/id/{0}", "https://www.linkedin.com/in/{0}" })
    Check($"Provider-specific input requirement is available for {url}", () =>
    {
        if (string.IsNullOrWhiteSpace(SourceCapabilities.GetHint(url))) throw new InvalidOperationException("Missing source hint.");
    });
Check("Custom domains cannot inherit provider hints by source name", () =>
{
    if (SourceCapabilities.GetHint("https://example.test/id/{0}") != null) throw new InvalidOperationException("Provider hint leaked to a custom host.");
});
Check("Unsupported Epic authentication lookup cannot offer a manual profile check", () =>
{
    if (SourceCapabilities.SupportsManualCheck("https://www.epicgames.com/id/{0}")) throw new InvalidOperationException("Unsupported manual lookup advertised.");
});
Check("Public user-profile URL can offer manual verification", () =>
{
    if (!SourceCapabilities.SupportsManualCheck("https://www.instagram.com/{0}/")) throw new InvalidOperationException("Public profile manual lookup missing.");
});
Check("Unknown providers retain conservative public lookup instead of being disabled", () =>
{
    if (!SourceCapabilities.SupportsPublicProfileLookup("https://example.test/{0}")) throw new InvalidOperationException("Unknown provider unexpectedly disabled.");
});
Check("Epic unsupported lookup capability is explicit", () =>
{
    if (SourceCapabilities.SupportsPublicProfileLookup("https://www.epicgames.com/id/{0}")) throw new InvalidOperationException("Unsupported lookup advertised.");
});

const string githubApi = "https://api.github.com/users/alice";
string validApi = "{\"login\":\"alice\",\"id\":9876,\"type\":\"User\",\"html_url\":\"https://github.com/alice\"}";
Check("GitHub API exact typed identity confirms the account", () => Status(SiteRules.EvaluateApi(validApi, githubApi, githubApi, source), AccountStatus.Found));
Check("GitHub API different login is not a match", () => Status(SiteRules.EvaluateApi(validApi.Replace("alice", "bob", StringComparison.Ordinal), githubApi, githubApi, source), AccountStatus.Unconfirmed));
Check("A full-name search cannot become an exact GitHub API account", () => Status(SiteRules.EvaluateApi(validApi, githubApi, githubApi,
    new MediaSource { Name = "Full-name API", LinkType = "FullName" }), AccountStatus.Unconfirmed));
Check("An API credential error is blocked, not an absent account", () => Status(SiteRules.EvaluateApi("{\"error\":\"invalid API key\"}", githubApi, githubApi, source), AccountStatus.Blocked));
foreach (var json in new[] { "null", "[]", "true", "{", "{\"data\":{\"id\":123}}", "{\"id\":123}", "{\"username\":\"alice\"}", "{\"ok\":false}", "{\"errors\":[]}" })
    Check($"Arbitrary/empty JSON never invents an account: {json}", () => Status(SiteRules.EvaluateApi(json, githubApi, githubApi, source), AccountStatus.Unconfirmed));
Check("Custom API cannot claim provider identity through copied fields", () => Status(SiteRules.EvaluateApi(validApi, "https://example.test/alice", "https://example.test/alice", source), AccountStatus.Unconfirmed));

using var preCancelled = new CancellationTokenSource(); preCancelled.Cancel();
Status(await SearchAlgorithm.CheckDetailedAsync(instagram, null, source, cancellationToken: preCancelled.Token), AccountStatus.Cancelled); passed++;
var delayed = FakeDriver.Create(elapsed => elapsed.TotalMilliseconds < 250 ? "<html>Loading…</html>" : Instagram());
var early = Stopwatch.StartNew();
var hydrated = await SearchAlgorithm.CheckDetailedAsync(instagram, null, source, driver: delayed.Driver);
Check("DOM polling returns early after delayed strong evidence", () =>
{
    Status(hydrated, AccountStatus.Found);
    if (early.Elapsed > TimeSpan.FromSeconds(2)) throw new InvalidOperationException("The check retained a fixed sleep.");
});
using var pollCancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
var loading = FakeDriver.Create(_ => "<html>Loading…</html>");
var cancelled = await SearchAlgorithm.CheckDetailedAsync(instagram, null, source, driver: loading.Driver, cancellationToken: pollCancel.Token);
Check("Cancellation interrupts DOM polling", () => Status(cancelled, AccountStatus.Cancelled));
var manualTransition = FakeDriver.Create(elapsed => elapsed.TotalMilliseconds < 300 ? "<h1>Please solve the captcha</h1>" : Instagram());
var manualElapsed = Stopwatch.StartNew();
using var manualDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
var manualFound = await SearchAlgorithm.CheckDetailedAsync(instagram, null, source, driver: manualTransition.Driver,
    cancellationToken: manualDeadline.Token, interactive: true);
Check("Interactive check waits through a challenge until a person-produced profile appears", () =>
{
    Status(manualFound, AccountStatus.Found);
    if (manualElapsed.Elapsed < TimeSpan.FromMilliseconds(300) || manualElapsed.Elapsed > TimeSpan.FromSeconds(2))
        throw new InvalidOperationException("Challenge waiting did not follow the bounded polling path.");
});
var manualChallenge = FakeDriver.Create(_ => "<h1>Please solve the captcha</h1>");
using var manualCancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
var manualCancelElapsed = Stopwatch.StartNew();
var manualCancelled = await SearchAlgorithm.CheckDetailedAsync(instagram, null, source, driver: manualChallenge.Driver,
    cancellationToken: manualCancel.Token, interactive: true);
Check("Interactive challenge waiting can be cancelled promptly", () =>
{
    Status(manualCancelled, AccountStatus.Cancelled);
    if (manualCancelElapsed.Elapsed > TimeSpan.FromSeconds(1)) throw new InvalidOperationException("Manual cancellation was delayed.");
});
var timeoutDriver = FakeDriver.Create(_ => "<html>Loading…</html>", navigationTimeout: true);
var timedOut = await SearchAlgorithm.CheckDetailedAsync(instagram, null, source, driver: timeoutDriver.Driver);
Check("Navigation timeout remains unknown and differs from user cancellation", () => Status(timedOut, AccountStatus.Unconfirmed));
var brokenDriver = FakeDriver.Create(_ => throw new WebDriverException("C:\\private\\profile\\secret"));
var broken = await SearchAlgorithm.CheckDetailedAsync(instagram, null, source, driver: brokenDriver.Driver);
Check("Driver failures do not expose raw exception paths", () =>
{
    Status(broken, AccountStatus.Error);
    if ((broken.Reason + broken.Evidence).Contains("private", StringComparison.Ordinal)) throw new InvalidOperationException("Exception leaked.");
});

await using var server = new FixtureServer();
server.Route = (path, hit) => path switch
{
    "/slow" => new(200, "{\"id\":1}", "application/json", 30000),
    "/blocked" => new(403, "<title>Access denied</title>", "text/html"),
    "/rate" => new(429, "<h1>Rate limit</h1>", "text/html"),
    "/missing" => new(404, "<title>Page not found</title>", "text/html"),
    "/alice" => hit == 1 ? new(404, "{}", "application/json") : new(200, "{\"username\":\"alice\",\"id\":123}", "application/json"),
    "/captcha" => new(200, "<h1>Please solve the captcha</h1>", "text/html"),
    _ => new(200, "<h1>alice</h1><p>followers profile bio</p>", "text/html")
};
foreach (var (path, expected) in new[] { ("blocked", AccountStatus.Blocked), ("rate", AccountStatus.Blocked), ("missing", AccountStatus.NotFound) })
{
    var apiSource = new MediaSource { Name = "Local API", ApiUrl = server.Url + path };
    var result = await SearchAlgorithm.CheckDetailedAsync(server.Url + path, null, apiSource);
    Check($"HTTP status /{path} survives an HTML body", () => Status(result, expected));
}
var localApi = new MediaSource { Name = "Local API", ApiUrl = server.Url + "alice" };
var first = await SearchAlgorithm.CheckDetailedAsync(server.Url + "alice", null, localApi);
var second = await SearchAlgorithm.CheckDetailedAsync(server.Url + "alice", null, localApi);
Check("Repeated URL gets a fresh response, not an indefinite cached result", () => { Status(first, AccountStatus.NotFound); Status(second, AccountStatus.Unconfirmed); });
using var bodyCancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
var apiCancelled = await SearchAlgorithm.CheckDetailedAsync(server.Url + "slow", null, new MediaSource { Name = "Slow", ApiUrl = server.Url + "slow" }, cancellationToken: bodyCancel.Token);
Check("Cancellation interrupts a pending HTTP body read", () => Status(apiCancelled, AccountStatus.Cancelled));
var manualApiDriver = FakeDriver.Create(_ => "<h1>User not found</h1>");
using var manualApiDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
var manualApi = await SearchAlgorithm.CheckDetailedAsync(server.Url + "interactive-api", null,
    new MediaSource { Name = "API with a browser profile", ApiUrl = server.Url + "interactive-api", SearchUrl = server.Url + "{0}" },
    driver: manualApiDriver.Driver, cancellationToken: manualApiDeadline.Token, interactive: true);
Check("Interactive mode checks the supplied DOM even when the source has an API URL", () => Status(manualApi, AccountStatus.NotFound));

if (args.Contains("--browser", StringComparer.Ordinal))
{
    try
    {
        using var driver = SearchAlgorithm.CreateChromeDriver("RegressionTests");
        var unknown = await SearchAlgorithm.CheckDetailedAsync(server.Url + "unknown", null, source, driver: driver);
        Check("Real Chrome rejects a generic local profile as unconfirmed", () => Status(unknown, AccountStatus.Unconfirmed));
        var missing = await SearchAlgorithm.CheckDetailedAsync(server.Url + "missing", null, source, driver: driver);
        Check("Real Chrome handles an explicit missing page", () => Status(missing, AccountStatus.NotFound));
        var captcha = await SearchAlgorithm.CheckDetailedAsync(server.Url + "captcha", null, source, driver: driver);
        Check("Real Chrome stops at CAPTCHA without bypassing it", () => Status(captcha, AccountStatus.Blocked));
        using var concurrent = SearchAlgorithm.CreateChromeDriver("RegressionTests");
        var other = await SearchAlgorithm.CheckDetailedAsync(server.Url + "captcha", null, source, driver: concurrent);
        Check("Two simultaneous Chrome sessions use independent temporary roots", () => Status(other, AccountStatus.Blocked));
        using var cancelBrowser = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
        var browserCancelled = await SearchAlgorithm.CheckDetailedAsync(server.Url + "unknown", null, source, driver: concurrent, cancellationToken: cancelBrowser.Token);
        Check("Real Chrome DOM wait observes cancellation", () => Status(browserCancelled, AccountStatus.Cancelled));
    }
    catch (Exception ex) { Check("Real Chrome fixture lifecycle", () => throw new InvalidOperationException(ex.GetBaseException().Message)); }
}

Console.WriteLine($"{passed} passed; {failed} failed.");
return failed == 0 ? 0 : 1;
