using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using MediaParser.Models;
using MediaParser.Models.DBModels;
using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;

namespace MediaParser.Classes;

public static class SearchAlgorithm
{
    private static readonly TimeSpan DomWait = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(200);

    public static string BuildSearchUrl(string template, string identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier))
            throw new ArgumentException("Вкажіть нікнейм або ПІБ.", nameof(identifier));
        if (string.IsNullOrWhiteSpace(template) || !template.Contains("{0}", StringComparison.Ordinal) || template.Contains('\\'))
            throw new ArgumentException("URL пошуку має містити {0} та не містити зворотних слешів.", nameof(template));
        string url = string.Format(CultureInfo.InvariantCulture, template, Uri.EscapeDataString(identifier.Trim()));
        if (!IsHttpUrl(url, out var uri))
            throw new ArgumentException("URL пошуку має бути абсолютною HTTP або HTTPS адресою.", nameof(template));
        return uri!.AbsoluteUri;
    }

    public static async Task<string> CheckAccountAsync(string url, string? apiKey, MediaSource media,
        string profile = "Default", IWebDriver? driver = null) =>
        (await CheckDetailedAsync(url, apiKey, media, profile, driver)).ToLegacyMessage();

    public static async Task<AccountCheckResult> CheckDetailedAsync(string url, string? apiKey, MediaSource media,
        string profile = "Default", IWebDriver? driver = null, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested) return Cancelled();
        if (!IsHttpUrl(url, out _))
            return new(AccountStatus.Error, "Некоректна адреса джерела: потрібен HTTP або HTTPS URL.");
        IWebDriver? ownedDriver = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.IsNullOrWhiteSpace(media.ApiUrl))
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(15));
                using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                request.Headers.UserAgent.ParseAdd("MediaParser/1.0");
                if (!string.IsNullOrWhiteSpace(apiKey))
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                return await ProcessApiResponseAsync(response, url, media, timeout.Token);
            }

            driver ??= ownedDriver = CreateChromeDriver(profile);
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                // Navigation is synchronous and bounded by PageLoad; cancellation is observed around it.
                driver.Navigate().GoToUrl(url);
            }
            catch (WebDriverTimeoutException)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var partial = SiteRules.EvaluateHtml(driver.PageSource, url, driver.Url, media);
                if (partial.Status != AccountStatus.Unconfirmed) return partial;
                return new(AccountStatus.Unconfirmed, "Сторінка не завершила завантаження за відведений час.", "Таймаут навігації браузера.");
            }

            var elapsed = Stopwatch.StartNew();
            AccountCheckResult result;
            do
            {
                cancellationToken.ThrowIfCancellationRequested();
                result = SiteRules.EvaluateHtml(driver.PageSource, url, driver.Url, media);
                if (result.Status != AccountStatus.Unconfirmed || media.LinkType == "FullName") return result;
                if (elapsed.Elapsed >= DomWait) break;
                await Task.Delay(PollInterval, cancellationToken);
            } while (true);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return Cancelled(); }
        catch (OperationCanceledException)
        { return new(AccountStatus.Unconfirmed, "Сервер не відповів за відведений час.", "Таймаут мережевого запиту."); }
        catch (HttpRequestException)
        { return new(AccountStatus.Error, "Не вдалося встановити з’єднання із сервером джерела.", "Мережева помилка; існування акаунта не визначено."); }
        catch (WebDriverTimeoutException)
        { return new(AccountStatus.Unconfirmed, "Браузер не відповів за відведений час.", "Таймаут команди WebDriver."); }
        catch (WebDriverException)
        { return new(AccountStatus.Error, "Не вдалося виконати перевірку в браузері.", "Перевірте доступність Chrome та сумісного ChromeDriver."); }
        catch (ArgumentException)
        { return new(AccountStatus.Error, "Некоректні параметри джерела або профілю браузера."); }
        catch (Exception)
        { return new(AccountStatus.Error, "Перевірку не вдалося завершити через внутрішню помилку."); }
        finally
        {
            if (ownedDriver != null)
            {
                try { ownedDriver.Quit(); } catch (WebDriverException) { }
                try { ownedDriver.Dispose(); } catch (WebDriverException) { }
            }
        }
    }

    private static async Task<AccountCheckResult> ProcessApiResponseAsync(HttpResponseMessage response, string url,
        MediaSource media, CancellationToken cancellationToken)
    {
        // Preserve HTTP status even when the body is an HTML gateway response.
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized or HttpStatusCode.TooManyRequests)
            return new(AccountStatus.Blocked, response.StatusCode == HttpStatusCode.TooManyRequests
                ? "Джерело обмежило частоту запитів." : "Джерело вимагає авторизації або заборонило доступ.", $"HTTP {(int)response.StatusCode}.");
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
            return new(AccountStatus.NotFound, "Запитаний ресурс не знайдено на сервері джерела.", $"HTTP {(int)response.StatusCode}.");
        if (!response.IsSuccessStatusCode)
            return new(AccountStatus.Error, "Сервер повернув помилку; існування акаунта не визначено.", $"HTTP {(int)response.StatusCode}.");

        string finalUrl = response.RequestMessage?.RequestUri?.AbsoluteUri ?? url;
        string content = await response.Content.ReadAsStringAsync(cancellationToken);
        string? type = response.Content.Headers.ContentType?.MediaType;
        if (type == null || !(type.Equals("application/json", StringComparison.OrdinalIgnoreCase) || type.EndsWith("+json", StringComparison.OrdinalIgnoreCase)))
        {
            var page = SiteRules.EvaluateHtml(content, url, finalUrl, media);
            return page.Status == AccountStatus.Blocked ? page :
                new(AccountStatus.Unconfirmed, "API повернув неочікуваний формат відповіді.", "Очікувався JSON; HTML не є доказом існування акаунта.");
        }
        return SiteRules.EvaluateApi(content, url, finalUrl, media);
    }

    public static IWebDriver CreateChromeDriver(string profile)
    {
        profile = string.IsNullOrWhiteSpace(profile) ? "Default" : profile.Trim();
        if (profile.Contains('/') || profile.Contains('\\') || profile is "." or ".." || profile.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("Вкажіть назву каталогу профілю Chrome, наприклад Default або Profile 1.", nameof(profile));
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SE_CACHE_PATH")))
            Environment.SetEnvironmentVariable("SE_CACHE_PATH", Path.Combine(Directory.GetCurrentDirectory(), ".cache", "selenium"));
        Environment.SetEnvironmentVariable("SE_AVOID_STATS", "true");
        var options = new ChromeOptions { PageLoadStrategy = PageLoadStrategy.Eager };
        // ChromeDriver creates and cleans a separate temporary user-data root for every session.
        options.AddArgument($"profile-directory={profile}");
        options.AddArgument("--disable-dev-shm-usage");
        options.AddArgument("--lang=en-US");
        options.AddArgument("--headless=new");
        var driver = new ChromeDriver(options);
        driver.Manage().Timeouts().PageLoad = TimeSpan.FromSeconds(15);
        driver.Manage().Timeouts().ImplicitWait = TimeSpan.Zero;
        return driver;
    }

    private static bool IsHttpUrl(string url, out Uri? uri) => Uri.TryCreate(url, UriKind.Absolute, out uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) && !url.Contains('\\');
    private static AccountCheckResult Cancelled() => new(AccountStatus.Cancelled, "Перевірку скасовано користувачем.");
}
