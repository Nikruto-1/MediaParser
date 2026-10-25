using System.Text.RegularExpressions;
using OpenQA.Selenium;

namespace MediaParser.Services.Captcha;

/// <summary>Supports public, visible v2/standalone widgets. Does not inspect cookies, page HTML or provider internals.</summary>
public sealed class BrowserCaptchaAutomation(ICaptchaSolver solver) : ICaptchaAutomation
{
    private const string WidgetSelector = ".g-recaptcha[data-sitekey], .cf-turnstile[data-sitekey]";
    private const string FrameSelector = "iframe[src]";
    private const string ManagedSelector = "#challenge-form, #cf-challenge-running, #cf-chl-widget, [data-chl-page-data]";
    private const string CallbackCheckScript = """
        /* MediaParser:captcha-callback-check */
        const name = arguments[0];
        const forbidden = new Set(['__proto__','prototype','constructor','eval','Function','document','location','fetch','open','postMessage','setTimeout','setInterval','submit','requestSubmit']);
        let parts = name.split('.');
        if (parts[0] === 'window') parts = parts.slice(1);
        if (!parts.length || parts.some(part => forbidden.has(part))) return false;
        let owner = window;
        for (let i = 0; i < parts.length; i++) {
          const entry = Object.getOwnPropertyDescriptor(owner, parts[i]);
          if (!entry || !('value' in entry)) return false;
          if (i === parts.length - 1) return typeof entry.value === 'function';
          if (!entry.value || typeof entry.value !== 'object') return false;
          owner = entry.value;
        }
        return false;
        """;

    private const string ApplyTokenScript = """
        /* MediaParser:captcha-token-apply */
        const [pageUrl, widget, field, frame, frameSource, siteKey, declaredKey, fieldName, callback, action, data, token] = arguments;
        const visible = node => {
          if (!node || !node.isConnected || node.hidden) return false;
          for (let current = node; current && current.nodeType === 1; current = current.parentElement) {
            const style = getComputedStyle(current);
            if (current.hidden || current.getAttribute('aria-hidden') === 'true' || style.display === 'none' || style.visibility === 'hidden' || style.visibility === 'collapse' || style.opacity === '0') return false;
          }
          const rect = node.getBoundingClientRect();
          return rect.width >= 10 && rect.height >= 10;
        };
        if (window.location.href !== pageUrl || !visible(widget) || !field || !field.isConnected || !widget.contains(field)) return false;
        if (Object.prototype.hasOwnProperty.call(window, '_cf_chl_opt') || document.querySelector('#challenge-form, #cf-challenge-running, #cf-chl-widget, [data-chl-page-data]')) return false;
        if (field.name !== fieldName || String(field.value || '').length !== 0 ||
            field.tagName === 'INPUT' && field.type !== 'hidden') return false;
        if ((widget.getAttribute('data-sitekey') || '') !== declaredKey ||
            (widget.getAttribute('data-callback') || '') !== callback ||
            (widget.getAttribute('data-action') || '') !== action ||
            (widget.getAttribute('data-cdata') || '') !== data ||
            (widget.getAttribute('data-size') || '').toLowerCase() === 'invisible') return false;
        const form = widget.closest('form');
        if (form && (form.querySelector('input[type="password"]') || /(?:^|\/)(?:login|signin|sign-in|auth)(?:\/|$)/i.test(new URL(form.action || pageUrl, pageUrl).pathname))) return false;
        if (frame && (!visible(frame) || frame.getAttribute('src') !== frameSource || !widget.contains(frame))) return false;
        if (fieldName === 'g-recaptcha-response') {
          if (!frame) return false;
          let url;
          try { url = new URL(frame.getAttribute('src')); } catch { return false; }
          if (url.searchParams.get('k') !== siteKey || (url.searchParams.get('size') || '').toLowerCase() === 'invisible') return false;
        }
        let callbackOwner = window, callbackFunction = null;
        if (callback) {
          let parts = callback.split('.');
          if (parts[0] === 'window') parts = parts.slice(1);
          const forbidden = new Set(['__proto__','prototype','constructor','eval','Function','document','location','fetch','open','postMessage','setTimeout','setInterval','submit','requestSubmit']);
          if (!parts.length || parts.some(part => forbidden.has(part))) return false;
          for (let i = 0; i < parts.length; i++) {
            const entry = Object.getOwnPropertyDescriptor(callbackOwner, parts[i]);
            if (!entry || !('value' in entry)) return false;
            if (i === parts.length - 1) { if (typeof entry.value !== 'function') return false; callbackFunction = entry.value; }
            else { if (!entry.value || typeof entry.value !== 'object') return false; callbackOwner = entry.value; }
          }
        }
        const prototype = field.tagName === 'TEXTAREA' ? HTMLTextAreaElement.prototype : field.tagName === 'INPUT' ? HTMLInputElement.prototype : null;
        const setter = prototype && Object.getOwnPropertyDescriptor(prototype, 'value')?.set;
        if (!setter) return false;
        setter.call(field, token);
        field.dispatchEvent(new Event('input', { bubbles: true }));
        field.dispatchEvent(new Event('change', { bubbles: true }));
        if (callbackFunction) { try { callbackFunction.call(callbackOwner, token); } catch { /* Token stays in its matching field; the next page check decides the outcome. */ } }
        return true;
        """;

    private static readonly Regex SiteKeyPattern = new(@"\A[A-Za-z0-9_-]{10,256}\z", RegexOptions.CultureInvariant);
    private static readonly Regex CallbackPattern = new(@"\A(?:window\.)?[A-Za-z_$][A-Za-z0-9_$]*(?:\.[A-Za-z_$][A-Za-z0-9_$]*)*\z", RegexOptions.CultureInvariant);
    private static readonly HashSet<string> ForbiddenCallbacks = new(StringComparer.Ordinal)
    {
        "__proto__", "prototype", "constructor", "eval", "Function", "document", "location", "fetch", "open",
        "postMessage", "setTimeout", "setInterval", "submit", "requestSubmit"
    };
    private static readonly HashSet<string> SensitiveQueryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "token", "accesstoken", "idtoken", "code", "apikey", "key", "password", "secret", "session", "sessionid",
        "auth", "authorization", "signature", "credential", "credentials", "csrf", "csrftoken"
    };

    public async Task<CaptchaAttemptResult> TrySolveAsync(IWebDriver driver, CaptchaSolveBudget budget,
        CancellationToken cancellationToken, Action<string>? onProgress = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!solver.IsAvailable) return new(false, false, "Автоматичне розв’язання капчі вимкнено або не налаштовано.");
        if (driver is not IJavaScriptExecutor script) return new(false, false, "Цей браузер не підтримує автоматичне розв’язання капчі.");
        var attempted = false;
        try
        {
            var candidate = Detect(driver, script);
            if (candidate == null)
                return new(false, false, "Підтримувану видиму капчу не визначено. Доступна ручна перевірка.");
            cancellationToken.ThrowIfCancellationRequested();
            if (!budget.TryReserve()) return new(false, false, "Ліміт автоматичних спроб для цього пошуку вичерпано.");
            attempted = true;
            Report(onProgress, "Розв’язуємо капчу через 2Captcha…");
            var solution = await solver.SolveAsync(new CaptchaChallenge(candidate.Kind, candidate.WebsiteUrl,
                candidate.SiteKey, candidate.Callback.Length == 0 ? null : candidate.Callback,
                Action: candidate.Action.Length == 0 ? null : candidate.Action,
                Data: candidate.Data.Length == 0 ? null : candidate.Data), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!solution.Success)
                return new(true, false, solution.ErrorCode switch
                {
                    "ERROR_ZERO_BALANCE" => "На рахунку 2Captcha недостатньо коштів. Перевірте баланс сервісу.",
                    "ERROR_KEY_DOES_NOT_EXIST" or "ERROR_WRONG_USER_KEY" => "API-ключ 2Captcha недійсний. Перевірте локальні налаштування.",
                    "TIMEOUT" => "Час очікування 2Captcha минув. Нову платну задачу автоматично не створено.",
                    "NETWORK_ERROR" or "HTTP_ERROR" => "Не вдалося отримати відповідь 2Captcha. Платний запит автоматично не повторюється.",
                    _ => "2Captcha не зміг розв’язати цю капчу. Скористайтеся ручною перевіркою."
                });
            if (!solution.Success || string.IsNullOrWhiteSpace(solution.Token) || solution.Token.Length > 32768 || solution.Token.Any(character => char.IsControl(character) || char.IsWhiteSpace(character)))
                return new(true, false, "Сервіс не надав придатну відповідь капчі. Скористайтеся ручною перевіркою.");
            Report(onProgress, "Відповідь капчі отримано; перевіряємо сторінку…");
            var current = Detect(driver, script);
            if (!SameChallenge(candidate, current))
                return new(true, false, "Сторінка або капча змінилася. Відповідь не застосовано; доступна ручна перевірка.");
            cancellationToken.ThrowIfCancellationRequested();
            var applied = script.ExecuteScript(ApplyTokenScript, candidate.PageUrl, candidate.Widget, candidate.Field,
                candidate.Frame, candidate.FrameSource, candidate.SiteKey, candidate.DeclaredKey, candidate.FieldName,
                candidate.Callback, candidate.Action, candidate.Data, solution.Token) is true;
            return new(true, applied, applied
                ? "Відповідь капчі застосовано. Існування профілю перевіряється окремо."
                : "Відповідь не застосовано до цієї сторінки. Скористайтеся ручною перевіркою.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            return new(attempted, false, "Автоматичну перевірку капчі не завершено. Скористайтеся ручною перевіркою.");
        }
    }

    private static Candidate? Detect(IWebDriver driver, IJavaScriptExecutor script)
    {
        var pageUrl = driver.Url;
        if (!TryPublicUrl(pageUrl, out var websiteUrl) || driver.FindElements(By.CssSelector(ManagedSelector)).Count != 0 ||
            script.ExecuteScript("/* MediaParser:captcha-managed-check */ return Object.prototype.hasOwnProperty.call(window, '_cf_chl_opt');") is true)
            return null;
        var candidates = new List<Candidate>();
        var widgets = driver.FindElements(By.CssSelector(WidgetSelector));
        foreach (var widget in widgets)
        {
            if (!Visible(widget) || UnsafeForm(widget, pageUrl) || Attribute(widget, "data-size").Equals("invisible", StringComparison.OrdinalIgnoreCase)) continue;
            var classes = Attribute(widget, "class").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (classes.Contains("g-recaptcha", StringComparer.Ordinal) && classes.Contains("cf-turnstile", StringComparer.Ordinal)) continue;
            var kind = classes.Contains("cf-turnstile", StringComparer.Ordinal) ? CaptchaKind.Turnstile : CaptchaKind.RecaptchaV2;
            if (kind == CaptchaKind.RecaptchaV2 && Attribute(widget, "data-action").Length != 0) continue;
            var key = Attribute(widget, "data-sitekey");
            var visibleFrames = widget.FindElements(By.CssSelector(FrameSelector)).Where(Visible).ToArray();
            var frame = visibleFrames.FirstOrDefault(value => TrustedFrame(value, kind, key));
            if (visibleFrames.Length > 0 && (visibleFrames.Length != 1 || frame == null)) continue;
            if (kind == CaptchaKind.RecaptchaV2 && frame == null) continue;
            if (kind == CaptchaKind.Turnstile && frame == null && !HasTurnstileScript(driver)) continue;
            var candidate = BuildCandidate(pageUrl, websiteUrl!, widget, frame, kind, key, script);
            if (candidate != null) candidates.Add(candidate);
        }
        // Explicit reCAPTCHA rendering can omit the conventional widget class. Associate only one local response field.
        foreach (var frame in driver.FindElements(By.CssSelector(FrameSelector)))
        {
            if (!Visible(frame) || !TryRecaptchaKey(Attribute(frame, "src"), out var key) ||
                candidates.Any(value => value.Frame?.Equals(frame) == true)) continue;
            var parent = frame;
            for (var depth = 0; depth < 4; depth++)
            {
                parent = parent.FindElements(By.XPath("..")).FirstOrDefault();
                if (parent == null || parent.TagName.Equals("body", StringComparison.OrdinalIgnoreCase) || parent.TagName.Equals("html", StringComparison.OrdinalIgnoreCase)) break;
                if (parent.FindElements(By.CssSelector(".g-recaptcha, .cf-turnstile")).Count != 0 ||
                    Attribute(parent, "class").Split(' ').Any(value => value is "g-recaptcha" or "cf-turnstile")) break;
                if (!Visible(parent) || UnsafeForm(parent, pageUrl)) continue;
                if (parent.FindElements(By.CssSelector(FrameSelector)).Count(value => Visible(value) && TryRecaptchaKey(Attribute(value, "src"), out _)) != 1) continue;
                var candidate = BuildCandidate(pageUrl, websiteUrl!, parent, frame, CaptchaKind.RecaptchaV2, key!, script);
                if (candidate != null) { candidates.Add(candidate); break; }
            }
        }
        return candidates.Count == 1 ? candidates[0] : null;
    }

    private static Candidate? BuildCandidate(string pageUrl, string websiteUrl, IWebElement widget, IWebElement? frame,
        CaptchaKind kind, string key, IJavaScriptExecutor script)
    {
        if (!SiteKeyPattern.IsMatch(key)) return null;
        var fieldName = kind == CaptchaKind.RecaptchaV2 ? "g-recaptcha-response" : "cf-turnstile-response";
        var fields = widget.FindElements(By.CssSelector($"textarea[name='{fieldName}'], input[name='{fieldName}']"));
        if (fields.Count != 1 || fields[0].TagName.Equals("input", StringComparison.OrdinalIgnoreCase) &&
            !Attribute(fields[0], "type").Equals("hidden", StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(fields[0].GetDomProperty("value"))) return null;
        var callback = Attribute(widget, "data-callback");
        if (callback.Length > 128 || callback.Length != 0 && (!CallbackPattern.IsMatch(callback) ||
            callback.Split('.').Any(ForbiddenCallbacks.Contains) || script.ExecuteScript(CallbackCheckScript, callback) is not true)) return null;
        var action = Attribute(widget, "data-action");
        var data = Attribute(widget, "data-cdata");
        if (!PublicWidgetValue(action, 32) || !PublicWidgetValue(data, 255)) return null;
        return new(pageUrl, websiteUrl, widget, fields[0], frame, frame == null ? "" : Attribute(frame, "src"),
            kind, key, Attribute(widget, "data-sitekey"), fieldName, callback, action, data);
    }

    private static bool TrustedFrame(IWebElement frame, CaptchaKind kind, string key)
    {
        var source = Attribute(frame, "src");
        if (kind == CaptchaKind.RecaptchaV2) return TryRecaptchaKey(source, out var frameKey) && frameKey == key;
        return Uri.TryCreate(source, UriKind.Absolute, out var uri) && uri.Scheme == "https" && uri.IsDefaultPort && uri.Host == "challenges.cloudflare.com" &&
            uri.AbsolutePath.Contains("/turnstile/", StringComparison.Ordinal) && string.IsNullOrEmpty(uri.UserInfo);
    }

    private static bool TryRecaptchaKey(string source, out string? key)
    {
        key = null;
        if (!Uri.TryCreate(source, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !uri.IsDefaultPort || uri.UserInfo.Length != 0 ||
            uri.Host is not ("www.google.com" or "www.recaptcha.net" or "recaptcha.google.com") || uri.AbsolutePath != "/recaptcha/api2/anchor") return false;
        var query = Query(uri.Query);
        key = query.GetValueOrDefault("k");
        return key != null && SiteKeyPattern.IsMatch(key) && !query.GetValueOrDefault("size", "").Equals("invisible", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasTurnstileScript(IWebDriver driver) => driver.FindElements(By.CssSelector("script[src]")).Any(element =>
        Uri.TryCreate(Attribute(element, "src"), UriKind.Absolute, out var uri) && uri.Scheme == "https" && uri.IsDefaultPort &&
        uri.Host == "challenges.cloudflare.com" && uri.AbsolutePath == "/turnstile/v0/api.js" && uri.UserInfo.Length == 0);

    private static bool Visible(IWebElement element) => element.Displayed && element.Size.Width >= 10 && element.Size.Height >= 10;
    private static string Attribute(IWebElement element, string name) => element.GetDomAttribute(name) ?? "";
    private static bool PublicWidgetValue(string value, int maxLength) => value.Length <= maxLength && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-');
    private static bool UnsafeForm(IWebElement widget, string pageUrl) => widget.FindElements(By.XPath("ancestor-or-self::form")).Any(form =>
        form.FindElements(By.CssSelector("input[type='password']")).Count != 0 ||
        Uri.TryCreate(new Uri(pageUrl), Attribute(form, "action"), out var uri) && uri.Segments.Any(value => value.Trim('/').ToLowerInvariant() is "login" or "signin" or "sign-in" or "auth"));

    private static bool TryPublicUrl(string url, out string? publicUrl)
    {
        publicUrl = null;
        if (url.Length > 4096 || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") ||
            uri.UserInfo.Length != 0 || uri.Host == "challenges.cloudflare.com" ||
            uri.Segments.Any(value => value.Trim('/').ToLowerInvariant() is "login" or "signin" or "sign-in" or "auth") ||
            Query(uri.Query).Keys.Any(name => SensitiveQueryNames.Contains(Regex.Replace(name, "[^A-Za-z0-9]", "")))) return false;
        publicUrl = new UriBuilder(uri) { Fragment = "" }.Uri.AbsoluteUri;
        return true;
    }

    private static Dictionary<string, string> Query(string query)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in query.TrimStart('?').Split(new[] { '&', ';' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            var name = Uri.UnescapeDataString(parts[0].Replace('+', ' '));
            if (values.ContainsKey(name)) { values[name] = ""; continue; }
            values[name] = parts.Length > 1 ? Uri.UnescapeDataString(parts[1].Replace('+', ' ')) : "";
        }
        return values;
    }

    private static bool SameChallenge(Candidate original, Candidate? current) => current != null &&
        original.PageUrl == current.PageUrl && original.Kind == current.Kind && original.SiteKey == current.SiteKey &&
        original.Callback == current.Callback && original.Action == current.Action && original.Data == current.Data &&
        original.FrameSource == current.FrameSource && original.DeclaredKey == current.DeclaredKey &&
        Equals(original.Frame, current.Frame) && original.Widget.Equals(current.Widget) && original.Field.Equals(current.Field);

    private static void Report(Action<string>? callback, string message) { try { callback?.Invoke(message); } catch (Exception) { } }

    private sealed record Candidate(string PageUrl, string WebsiteUrl, IWebElement Widget, IWebElement Field,
        IWebElement? Frame, string FrameSource, CaptchaKind Kind, string SiteKey, string DeclaredKey,
        string FieldName, string Callback, string Action, string Data);
}
