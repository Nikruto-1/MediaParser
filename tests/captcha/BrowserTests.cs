using System.Collections.ObjectModel;
using System.Drawing;
using System.Reflection;
using MediaParser.Classes;
using MediaParser.Models;
using MediaParser.Models.DBModels;
using MediaParser.Services.Captcha;
using OpenQA.Selenium;

public static class CaptchaBrowserRegression
{
    public static async Task RunAsync()
    {
        int passed = 0;
        async Task Check(string name, Func<Task> action)
        {
            await action(); passed++; Console.WriteLine("PASS Browser: " + name);
        }
        await Check("Disabled provider does not inspect the browser or reserve a task", async () =>
        {
            var fixture = new BrowserFixture();
            var solver = new BrowserSolver { IsAvailable = false };
            var budget = new CaptchaSolveBudget(1);
            var result = await new BrowserCaptchaAutomation(solver).TrySolveAsync(fixture.Driver, budget, CancellationToken.None);
            Assert(!result.Attempted && !result.TokenApplied && budget.Used == 0 && solver.Calls == 0 && fixture.Reads == 0);
        });
        await Check("Visible reCAPTCHA uses public widget parameters and applies token as an argument", async () =>
        {
            var fixture = new BrowserFixture { PageUrl = "https://example.test/alice#local-fragment" };
            fixture.Widget.Attributes["data-callback"] = "page.onCaptcha";
            var solver = new BrowserSolver();
            var messages = new List<string>();
            var result = await new BrowserCaptchaAutomation(solver).TrySolveAsync(fixture.Driver, new CaptchaSolveBudget(1), CancellationToken.None, messages.Add);
            Assert(result.Attempted && result.TokenApplied && fixture.Applies == 1 && fixture.Token == "mock-token");
            Assert(solver.Last is { Kind: CaptchaKind.RecaptchaV2, WebsiteUrl: "https://example.test/alice", Callback: "page.onCaptcha" });
            Assert(solver.Last!.SiteKey == BrowserFixture.SiteKey && messages.Count == 2);
            Assert(!fixture.ApplyScript!.Contains("mock-token", StringComparison.Ordinal));
        });
        await Check("Standalone Turnstile uses its own response field", async () =>
        {
            var fixture = new BrowserFixture(turnstile: true);
            var solver = new BrowserSolver();
            var result = await new BrowserCaptchaAutomation(solver).TrySolveAsync(fixture.Driver, new CaptchaSolveBudget(1), CancellationToken.None);
            Assert(result.TokenApplied && solver.Last?.Kind == CaptchaKind.Turnstile && fixture.FieldName == "cf-turnstile-response");
        });
        await Check("Unknown, hidden, mismatched and invisible widgets do not reserve paid tasks", async () =>
        {
            Action<BrowserFixture>[] changes =
            [
                fixture => fixture.HasWidget = false,
                fixture => fixture.Widget.Displayed = false,
                fixture => fixture.Widget.Attributes["data-size"] = "invisible",
                fixture => fixture.Frame.Attributes["src"] = "https://www.google.com.evil.test/recaptcha/api2/anchor?k=" + BrowserFixture.SiteKey,
                fixture => fixture.Frame.Attributes["src"] = "https://www.google.com:8443/recaptcha/api2/anchor?k=" + BrowserFixture.SiteKey,
                fixture => fixture.Frame.Attributes["src"] = "https://www.google.com/recaptcha/api2/anchor?k=another-public-sitekey",
                fixture => fixture.HasField = false,
                fixture => fixture.Widget.Attributes["data-callback"] = "window.eval",
                fixture => fixture.Field.Value = "already-filled-token",
                fixture => fixture.Managed = true,
                fixture => fixture.FormAction = "/LOGIN",
                fixture => fixture.PasswordField = true,
                fixture => fixture.PageUrl = "https://example.test/login",
                fixture => fixture.PageUrl = "https://user:private@example.test/alice",
                fixture => fixture.PageUrl = "https://example.test/alice?access_token=private",
                fixture => fixture.PageUrl = "https://example.test/alice?public=x;api_key=private"
            ];
            foreach (var change in changes)
            {
                var fixture = new BrowserFixture(); change(fixture);
                var solver = new BrowserSolver(); var budget = new CaptchaSolveBudget(1);
                var result = await new BrowserCaptchaAutomation(solver).TrySolveAsync(fixture.Driver, budget, CancellationToken.None);
                Assert(!result.TokenApplied && solver.Calls == 0 && budget.Used == 0);
            }
        });
        await Check("Changed page or sitekey discards a paid response before injection", async () =>
        {
            foreach (bool navigate in new[] { false, true })
            {
                var fixture = new BrowserFixture();
                var solver = new BrowserSolver { OnSolve = () => { if (navigate) fixture.PageUrl += "/other"; else fixture.Widget.Attributes["data-sitekey"] = "new-public-sitekey"; } };
                var result = await new BrowserCaptchaAutomation(solver).TrySolveAsync(fixture.Driver, new CaptchaSolveBudget(1), CancellationToken.None);
                Assert(result.Attempted && !result.TokenApplied && fixture.Applies == 0 && solver.Calls == 1);
            }
        });
        await Check("A shared exhausted budget never creates a second solve", async () =>
        {
            var fixture = new BrowserFixture(); var solver = new BrowserSolver(); var budget = new CaptchaSolveBudget(1);
            var automation = new BrowserCaptchaAutomation(solver);
            Assert((await automation.TrySolveAsync(fixture.Driver, budget, CancellationToken.None)).TokenApplied);
            fixture.Field.Value = "";
            var again = await automation.TrySolveAsync(fixture.Driver, budget, CancellationToken.None);
            Assert(!again.Attempted && !again.TokenApplied && solver.Calls == 1 && budget.Used == 1);
        });
        await Check("Provider errors are useful without exposing raw messages or tokens", async () =>
        {
            var fixture = new BrowserFixture();
            var solver = new BrowserSolver { Result = new(false, null, "ERROR_ZERO_BALANCE", "private-api-key") };
            var result = await new BrowserCaptchaAutomation(solver).TrySolveAsync(fixture.Driver, new CaptchaSolveBudget(1), CancellationToken.None);
            Assert(result.Attempted && !result.TokenApplied && fixture.Applies == 0 && result.Message!.Contains("коштів", StringComparison.Ordinal));
            Assert(!result.Message!.Contains("private", StringComparison.Ordinal));
        });
        await Check("Cancellation after a provider response never injects the token", async () =>
        {
            using var cancelled = new CancellationTokenSource();
            var fixture = new BrowserFixture(); var solver = new BrowserSolver { OnSolve = cancelled.Cancel };
            try
            {
                await new BrowserCaptchaAutomation(solver).TrySolveAsync(fixture.Driver, new CaptchaSolveBudget(1), cancelled.Token);
                throw new InvalidOperationException("Cancellation was ignored.");
            }
            catch (OperationCanceledException) when (cancelled.IsCancellationRequested) { }
            Assert(fixture.Applies == 0);
        });
        await Check("A malformed token cannot enter the page", async () =>
        {
            var fixture = new BrowserFixture();
            var solver = new BrowserSolver { Result = new(true, "token\nwith\ncontrols", null, "private") };
            var result = await new BrowserCaptchaAutomation(solver).TrySolveAsync(fixture.Driver, new CaptchaSolveBudget(1), CancellationToken.None);
            Assert(result.Attempted && !result.TokenApplied && fixture.Applies == 0);
        });
        Console.WriteLine($"{passed} browser adapter regression tests passed (fake DOM and solver only).");
    }

    public static async Task RunBrowserAsync()
    {
        // srcdoc wins over src: this looks like a widget to the adapter but loads only local HTML.
        // No real CAPTCHA, Google request, provider account, or payment is involved.
        const string page = """
            <!doctype html><html><body>
            <h1>Local adapter fixture</h1>
            <div class="g-recaptcha" data-sitekey="fixture-public-sitekey" data-callback="fixturePassed" style="width:320px;height:100px">
              <iframe width="304" height="78" src="https://www.google.com/recaptcha/api2/anchor?k=fixture-public-sitekey" srcdoc="<p>Local dummy widget only</p>"></iframe>
              <textarea name="g-recaptcha-response" style="display:none"></textarea>
            </div>
            <script>function fixturePassed(token) { if(token === 'mock-token') document.body.innerHTML = '<h1>User not found</h1>'; }</script>
            </body></html>
            """;
        await using var server = new FixtureServer { Route = (_, _) => new(200, page, "text/html") };
        using var driver = SearchAlgorithm.CreateChromeDriver("CaptchaLocalFixture");
        var solver = new BrowserSolver();
        var result = await SearchAlgorithm.CheckDetailedAsync(server.Url + "alice", null,
            new MediaSource { Name = "Local fixture", SearchUrl = server.Url + "{0}", LinkType = "User" }, driver: driver,
            captchaAutomation: new BrowserCaptchaAutomation(solver), captchaBudget: new CaptchaSolveBudget(1));
        Assert(result.Status == AccountStatus.NotFound && solver.Calls == 1);
        Console.WriteLine("PASS Real Chrome local fixture: adapter detects, injects, invokes callback and rechecks site evidence; no external CAPTCHA or paid request.");
        driver.Quit();
    }

    private static void Assert(bool condition) { if (!condition) throw new InvalidOperationException("Browser CAPTCHA regression assertion failed."); }
}

public interface ITestCaptchaDriver : IWebDriver, IJavaScriptExecutor { }

public class CaptchaDriverProxy : DispatchProxy
{
    public BrowserFixture Fixture { get; set; } = null!;
    protected override object? Invoke(MethodInfo? method, object?[]? arguments)
    {
        Fixture.Reads++;
        return method?.Name switch
        {
            "get_Url" => Fixture.PageUrl,
            "FindElements" => Fixture.Find(arguments![0]!.ToString()!),
            "ExecuteScript" => Fixture.Script((string)arguments![0]!, (object?[])arguments[1]!),
            _ => throw new NotSupportedException(method?.Name)
        };
    }
}

public sealed class BrowserFixture
{
    public const string SiteKey = "public-test-sitekey";
    public IWebDriver Driver { get; }
    public string PageUrl { get; set; } = "https://example.test/alice";
    public bool HasWidget { get; set; } = true;
    public bool HasField { get; set; } = true;
    public bool Managed { get; set; }
    public string? FormAction { get; set; }
    public bool PasswordField { get; set; }
    public string FieldName { get; }
    public int Reads { get; set; }
    public int Applies { get; private set; }
    public string? ApplyScript { get; private set; }
    public string? Token { get; private set; }
    public CaptchaElement Widget { get; }
    public CaptchaElement Frame { get; }
    public CaptchaElement Field { get; }
    public CaptchaElement Form { get; }

    public BrowserFixture(bool turnstile = false)
    {
        var proxy = DispatchProxy.Create<ITestCaptchaDriver, CaptchaDriverProxy>();
        ((CaptchaDriverProxy)proxy).Fixture = this; Driver = proxy;
        FieldName = turnstile ? "cf-turnstile-response" : "g-recaptcha-response";
        Field = new(this, "textarea"); Field.Attributes["name"] = FieldName;
        Frame = new(this, "iframe"); Frame.Attributes["src"] = turnstile
            ? "https://challenges.cloudflare.com/turnstile/v0/widget"
            : "https://www.google.com/recaptcha/api2/anchor?k=" + SiteKey;
        Widget = new(this, "div"); Widget.Attributes["class"] = turnstile ? "cf-turnstile" : "g-recaptcha"; Widget.Attributes["data-sitekey"] = SiteKey;
        Form = new(this, "form");
    }

    public ReadOnlyCollection<IWebElement> Find(string selector)
    {
        if (selector.Contains("#challenge-form", StringComparison.Ordinal)) return Elements(Managed ? Widget : null);
        if (selector.Contains(".g-recaptcha[data-sitekey]", StringComparison.Ordinal)) return Elements(HasWidget ? Widget : null);
        if (selector.Contains("iframe[src]", StringComparison.Ordinal)) return Elements(HasWidget ? Frame : null);
        return Elements();
    }

    public object Script(string source, object?[] args)
    {
        if (source.Contains("captcha-managed-check", StringComparison.Ordinal)) return Managed;
        if (source.Contains("captcha-callback-check", StringComparison.Ordinal)) return true;
        if (source.Contains("captcha-token-apply", StringComparison.Ordinal))
        {
            AssertReference(args[0] as string == PageUrl && ReferenceEquals(args[1], Widget) && ReferenceEquals(args[2], Field));
            Applies++; ApplyScript = source; Token = args[^1] as string; Field.Value = Token ?? ""; return true;
        }
        throw new NotSupportedException("Unexpected browser script.");
    }
    private static void AssertReference(bool condition) { if (!condition) throw new InvalidOperationException("Wrong injection target."); }
    public static ReadOnlyCollection<IWebElement> Elements(params IWebElement?[] values) => new(values.OfType<IWebElement>().ToList());
}

public sealed class CaptchaElement(BrowserFixture fixture, string tagName) : IWebElement
{
    public Dictionary<string, string> Attributes { get; } = new();
    public string Value { get; set; } = "";
    public string TagName => tagName;
    public string Text => "";
    public bool Enabled => true;
    public bool Selected => false;
    public Point Location => new(0, 0);
    public Size Size => new(304, 78);
    public bool Displayed { get; set; } = true;
    public string GetAttribute(string name) => GetDomAttribute(name) ?? "";
    public string? GetDomAttribute(string name) => ReferenceEquals(this, fixture.Form) && name == "action" ? fixture.FormAction : Attributes.GetValueOrDefault(name);
    public string GetDomProperty(string name) => name == "value" ? Value : "";
    public string GetCssValue(string propertyName) => "";
    public IWebElement FindElement(By by) => FindElements(by).Single();
    public ReadOnlyCollection<IWebElement> FindElements(By by)
    {
        string query = by.ToString();
        if (query.Contains("ancestor-or-self::form", StringComparison.Ordinal)) return BrowserFixture.Elements(fixture.FormAction != null || fixture.PasswordField ? fixture.Form : null);
        if (ReferenceEquals(this, fixture.Form)) return BrowserFixture.Elements(fixture.PasswordField ? fixture.Field : null);
        if (ReferenceEquals(this, fixture.Frame) && query.Contains("..", StringComparison.Ordinal)) return BrowserFixture.Elements(fixture.Widget);
        if (ReferenceEquals(this, fixture.Widget))
        {
            if (query.Contains("iframe[src]", StringComparison.Ordinal)) return BrowserFixture.Elements(fixture.Frame);
            if (query.Contains("response", StringComparison.Ordinal)) return BrowserFixture.Elements(fixture.HasField ? fixture.Field : null);
        }
        return BrowserFixture.Elements();
    }
    public ISearchContext GetShadowRoot() => throw new NotSupportedException();
    public void Clear() => throw new NotSupportedException();
    public void Click() => throw new NotSupportedException();
    public void SendKeys(string text) => throw new NotSupportedException();
    public void Submit() => throw new NotSupportedException();
}

public sealed class BrowserSolver : ICaptchaSolver
{
    public bool IsAvailable { get; set; } = true;
    public int Calls { get; private set; }
    public CaptchaChallenge? Last { get; private set; }
    public Action? OnSolve { get; set; }
    public CaptchaSolution Result { get; set; } = new(true, "mock-token", null, "Mock result.");
    public Task<CaptchaSolution> SolveAsync(CaptchaChallenge challenge, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested(); Calls++; Last = challenge; OnSolve?.Invoke(); return Task.FromResult(Result);
    }
}
