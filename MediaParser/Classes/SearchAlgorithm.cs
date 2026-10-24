using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;
using OpenQA.Selenium;
using OpenQA.Selenium.Chrome;
using MediaParser.Models.DBModels;
using HtmlAgilityPack;

namespace MediaParser.Classes
{
    public static class SearchAlgorithm
    {
        private static class ResultMessages
        {
            public const string Timeout = "❌ Таймаут запиту";
            public const string CaptchaRequired = "⚠️ Потрібна капча – пропуск";
            public const string NotFound = "❌ 404 (Не знайдено)";
            public const string TooManyRequests = "⚠️ 429 (Забагато запитів) – занадто багато запитів";
            public const string Forbidden = "⚠️ 403 (Заборонено) – доступ заборонено";
            public const string Unauthorized = "⚠️ 401 (Неавторизовано) – потрібна авторизація";
            public const string Success = "✅ Акаунт існує";
            public const string ErrorTextFound = "⚠️ Виявлено помилку: ";
            public const string Unconfirmed = "⚠️ Не вдалося підтвердити існування акаунта";
            public const string SearchResults = "🔎 Результати пошуку: потрібна перевірка знайдених профілів";
            public const string UnknownStatus = "❌ {0} – невідомий статус";
            public const string RequestError = "❌ Помилка запиту: {0}";
            public const string UnknownError = "❌ Невідома помилка: {0}";
            public const string RedirectDetected = "⚠️ Перенаправлення – існування акаунта не підтверджено";
            public const string InvalidContentType = "⚠️ Неочікуваний тип відповіді API – існування акаунта не підтверджено";
        }

        public static string BuildSearchUrl(string template, string identifier)
        {
            if (string.IsNullOrWhiteSpace(identifier))
                throw new ArgumentException("Вкажіть нікнейм або ПІБ.", nameof(identifier));
            if (string.IsNullOrWhiteSpace(template) || !template.Contains("{0}", StringComparison.Ordinal) || template.Contains('\\'))
                throw new ArgumentException("URL пошуку має містити {0} та не містити зворотних слешів.", nameof(template));

            string url = string.Format(System.Globalization.CultureInfo.InvariantCulture, template, Uri.EscapeDataString(identifier.Trim()));
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                throw new ArgumentException("URL пошуку має бути абсолютною HTTP або HTTPS адресою.", nameof(template));
            return uri.AbsoluteUri;
        }

        // Перевірка існування акаунту з можливістю передачі WebDriver
        public static async Task<string> CheckAccountAsync(string url, string? apiKey, MediaSource media, string profile, IWebDriver? driver = null)
        {
            for (int attempt = 0; attempt <= 2; attempt++)
            {
                try
                {
                    if (!string.IsNullOrWhiteSpace(media?.ApiUrl)) // Запит через API
                    {
                        using var client = CreateHttpClient(apiKey, true);
                        using var response = await client.GetAsync(url);
                        var result = await ProcessApiResponseAsync(response, url, media);
                        return result;
                    }
                    else if (driver != null) // Веб-запит із переданим драйвером
                    {
                        driver.Navigate().GoToUrl(url);
                        await Task.Delay(3000); // Очікування завантаження сторінки
                        var result = ProcessWebResponse(driver, url, media);
                        return result;
                    }
                    else // Веб-запит із новим драйвером (резервний варіант)
                    {
                        using var newDriver = CreateChromeDriver(profile);
                        newDriver.Navigate().GoToUrl(url);
                        await Task.Delay(3000);
                        var result = ProcessWebResponse(newDriver, url, media);
                        return result;
                    }
                }
                catch (TaskCanceledException)
                {
                    if (attempt == 2) return FormatResult(ResultMessages.Timeout, media);
                    await Task.Delay(1000 * (attempt + 1)); // Експоненційна затримка
                }
                catch (WebDriverTimeoutException)
                {
                    if (attempt == 2) return FormatResult(ResultMessages.Timeout, media);
                    await Task.Delay(1000 * (attempt + 1));
                }
                catch (HttpRequestException ex)
                {
                    return FormatResult(string.Format(ResultMessages.RequestError, ex.Message), media);
                }
                catch (WebDriverException ex)
                {
                    return FormatResult(string.Format(ResultMessages.RequestError, ex.Message), media);
                }
                catch (Exception ex)
                {
                    return FormatResult(string.Format(ResultMessages.UnknownError, ex.Message), media);
                }
            }

            return FormatResult(ResultMessages.Unconfirmed, media);
        }

        // Створення HTTP-клієнта
        private static HttpClient CreateHttpClient(string? apiKey, bool isApiRequest)
        {
            var client = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(15)
            };
            client.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/91.0.4472.124 Safari/537.36");
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue(isApiRequest ? "application/json" : "text/html"));
            client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.5");
            client.DefaultRequestHeaders.Referrer = new Uri("https://www.google.com");

            if (!string.IsNullOrEmpty(apiKey))
            {
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            }

            return client;
        }

        // Створення ChromeDriver (зроблено public для виклику з контролера)
        public static IWebDriver CreateChromeDriver(string profile)
        {
            profile = string.IsNullOrWhiteSpace(profile) ? "Default" : profile.Trim();
            if (profile.Contains('/') || profile.Contains('\\') || profile is "." or ".." || profile.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new ArgumentException("Вкажіть назву каталогу профілю Chrome, наприклад Default або Profile 1.", nameof(profile));

            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SE_CACHE_PATH")))
                Environment.SetEnvironmentVariable("SE_CACHE_PATH", Path.Combine(Directory.GetCurrentDirectory(), ".cache", "selenium"));
            Environment.SetEnvironmentVariable("SE_AVOID_STATS", "true");

            var options = new ChromeOptions();
            // ChromeDriver supplies a separate temporary user-data directory per session.
            options.AddArgument($"profile-directory={profile}");
            options.AddArgument("--disable-dev-shm-usage");
            options.AddArgument("--lang=en-US");
            options.AddArgument("--headless=new");

            var driver = new ChromeDriver(options);
            driver.Manage().Timeouts().PageLoad = TimeSpan.FromSeconds(15);

            return driver;
        }

        // Обробка відповіді API
        private static async Task<string> ProcessApiResponseAsync(HttpResponseMessage response, string url, MediaSource? media)
        {
            string content = await response.Content.ReadAsStringAsync();
            string finalUrl = response.RequestMessage?.RequestUri?.ToString() ?? url;

            // HTTP errors remain meaningful even when the response body is an HTML gateway page.
            if (!response.IsSuccessStatusCode)
            {
                return response.StatusCode switch
                {
                    HttpStatusCode.NotFound => FormatResult(ResultMessages.NotFound, media),
                    HttpStatusCode.TooManyRequests => FormatResult(ResultMessages.TooManyRequests, media),
                    HttpStatusCode.Forbidden => FormatResult(ResultMessages.Forbidden, media),
                    HttpStatusCode.Unauthorized => FormatResult(ResultMessages.Unauthorized, media),
                    HttpStatusCode.BadRequest => AnalyzeBadRequest(content, finalUrl, url, media),
                    _ => FormatResult(string.Format(ResultMessages.UnknownStatus, (int)response.StatusCode), media)
                };
            }

            // Перевірка капчі
            if (finalUrl.Contains("captcha", StringComparison.OrdinalIgnoreCase) ||
                content.Contains("please solve the captcha", StringComparison.OrdinalIgnoreCase) ||
                content.Contains("human verification", StringComparison.OrdinalIgnoreCase))
            {
                return FormatResult(ResultMessages.CaptchaRequired, media);
            }

            // Перевірка перенаправлення на головну сторінку
            if (IsUnrelatedRedirect(url, finalUrl, media))
            {
                return FormatResult(ResultMessages.RedirectDetected, media);
            }

            // Перевірка типу вмісту
            var contentType = response.Content.Headers.ContentType?.MediaType;
            if (!string.Equals(contentType, "application/json", StringComparison.OrdinalIgnoreCase) &&
                !(contentType?.EndsWith("+json", StringComparison.OrdinalIgnoreCase) ?? false))
            {
                return FormatResult(ResultMessages.InvalidContentType, media);
            }

            return AnalyzeContent(content, finalUrl, url, media);
        }

        // Обробка веб-відповіді
        private static string ProcessWebResponse(IWebDriver driver, string url, MediaSource? media)
        {
            string content = driver.PageSource;
            string finalUrl = driver.Url;

            // Перевірка капчі
            if (IsCaptchaPresent(driver, finalUrl, content))
            {
                return FormatResult(ResultMessages.CaptchaRequired, media);
            }

            // Перевірка перенаправлення
            if (IsUnrelatedRedirect(url, finalUrl, media))
            {
                return FormatResult(ResultMessages.RedirectDetected, media);
            }

            return AnalyzeContent(content, finalUrl, url, media);
        }

        private static bool IsUnrelatedRedirect(string originalUrl, string finalUrl, MediaSource? media)
        {
            var original = new Uri(originalUrl);
            var final = new Uri(finalUrl);
            if (original == final) return false;
            string originalHost = original.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? original.Host[4..] : original.Host;
            string finalHost = final.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? final.Host[4..] : final.Host;
            if (!string.Equals(original.Host, final.Host, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(originalHost, finalHost, StringComparison.OrdinalIgnoreCase))
                return true;

            if (final.Segments.Any(segment => new[] { "login", "signin", "checkpoint", "challenge", "auth" }
                .Contains(segment.Trim('/'), StringComparer.OrdinalIgnoreCase)))
                return true;

            if (media?.LinkType == "FullName")
                return !string.Equals(original.AbsolutePath.TrimEnd('/'), final.AbsolutePath.TrimEnd('/'), StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(original.Query, final.Query, StringComparison.OrdinalIgnoreCase);

            string nickname = Uri.UnescapeDataString(original.Segments.Last()).Trim('/').Trim('@');
            return string.IsNullOrEmpty(nickname) || !final.Segments.Any(segment =>
                string.Equals(Uri.UnescapeDataString(segment).Trim('/').Trim('@'), nickname, StringComparison.OrdinalIgnoreCase));
        }

        // Перевірка наявності капчі
        private static bool IsCaptchaPresent(IWebDriver driver, string finalUrl, string content)
        {
            // 1. Перевірка URL на типові шаблони капчі
            if (finalUrl.Contains("/captcha/", StringComparison.OrdinalIgnoreCase) ||
                finalUrl.Contains("captcha=", StringComparison.OrdinalIgnoreCase) ||
                finalUrl.EndsWith("captcha", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // 2. Перевірка наявності активних елементів капчі в DOM
            try
            {
                var captchaElements = driver.FindElements(By.XPath("//iframe[contains(@src, 'recaptcha')] | " +
                                                                  "//div[contains(@class, 'captcha')] | " +
                                                                  "//form[contains(@action, 'captcha')]"));
                if (captchaElements.Any(element => element.Displayed))
                {
                    return true;
                }
            }
            catch (NoSuchElementException)
            {
                // Елементи не знайдено, продовжуємо
            }

            // 3. Перевірка тексту в контенті з урахуванням контексту
            if (content.Contains("verify you are not a robot", StringComparison.OrdinalIgnoreCase) ||
                content.Contains("please solve the captcha", StringComparison.OrdinalIgnoreCase) ||
                content.Contains("human verification", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // Якщо "captcha" є, але сторінка робоча (містить ім'я користувача), ігноруємо
            string nickname = new Uri(finalUrl).Segments.Last().Trim('/').Trim('@');
            if (content.Contains(nickname, StringComparison.OrdinalIgnoreCase) &&
                content.Contains("captcha", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return false;
        }

        // Аналіз вмісту
        private static string AnalyzeContent(string content, string finalUrl, string originalUrl, MediaSource? media)
        {
            // Аналіз відповіді API
            if (!string.IsNullOrWhiteSpace(media?.ApiUrl))
            {
                try
                {
                    var json = JsonSerializer.Deserialize<JsonElement>(content);
                    if (json.ValueKind != JsonValueKind.Object)
                        return FormatResult(ResultMessages.Unconfirmed, media);

                    foreach (var field in new[] { "message", "description", "error", "errors" })
                    {
                        if (json.TryGetProperty(field, out var value) && IsMeaningful(value))
                        {
                            string message = value.ToString();
                            if (message.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
                                message.Contains("does not exist", StringComparison.OrdinalIgnoreCase))
                                return FormatResult(ResultMessages.NotFound, media);
                            if (field is "error" or "errors")
                                return FormatResult(ResultMessages.ErrorTextFound + message, media);
                        }
                    }

                    if (json.TryGetProperty("ok", out var okProp) && okProp.ValueKind == JsonValueKind.False)
                        return FormatResult(ResultMessages.Unconfirmed, media);

                    bool hasAccount = new[] { "username", "id", "data" }.Any(field =>
                        json.TryGetProperty(field, out var value) && IsMeaningful(value));
                    return FormatResult(hasAccount ? ResultMessages.Success : ResultMessages.Unconfirmed, media);
                }
                catch (JsonException)
                {
                    return FormatResult(ResultMessages.Unconfirmed, media);
                }
            }

            // Script bundles and CSS may contain error phrases even on a valid profile.
            var document = new HtmlDocument();
            document.LoadHtml(content);
            foreach (var node in document.DocumentNode.SelectNodes("//script|//style|//noscript")?.ToList() ?? new List<HtmlNode>())
                node.Remove();
            string text = HtmlEntity.DeEntitize(document.DocumentNode.InnerText);
            string title = HtmlEntity.DeEntitize(document.DocumentNode.SelectSingleNode("//title")?.InnerText ?? "");

            var foundErrors = (media?.ErrorKeywords ?? Array.Empty<string>())
                .Where(keyword => !string.IsNullOrWhiteSpace(keyword) &&
                    !keyword.Equals("404", StringComparison.OrdinalIgnoreCase) &&
                    !keyword.Equals("This account is private", StringComparison.OrdinalIgnoreCase) &&
                    !keyword.Equals("No videos found", StringComparison.OrdinalIgnoreCase) &&
                    text.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (foundErrors.Any())
                return FormatResult(ResultMessages.ErrorTextFound + string.Join(", ", foundErrors), media);

            if (title.Contains("Page not found", StringComparison.OrdinalIgnoreCase) || title.Trim().StartsWith("404", StringComparison.OrdinalIgnoreCase))
                return FormatResult(ResultMessages.NotFound, media);

            // A full-name search returns a list, not evidence that one specific account exists.
            if (media?.LinkType == "FullName")
                return FormatResult(ResultMessages.SearchResults, media);

            string nickname = Uri.UnescapeDataString(new Uri(originalUrl).Segments.Last()).Trim('/').Trim('@');
            bool hasNickname = !string.IsNullOrEmpty(nickname) && text.Contains(nickname, StringComparison.OrdinalIgnoreCase);
            bool hasProfileDetails = new[] { "profile", "followers", "repositories", "posts", "friends", "joined", "bio" }
                .Any(keyword => text.Contains(keyword, StringComparison.OrdinalIgnoreCase));
            bool privateProfile = text.Contains("This account is private", StringComparison.OrdinalIgnoreCase);

            return FormatResult(hasNickname && (hasProfileDetails || privateProfile) ? ResultMessages.Success : ResultMessages.Unconfirmed, media);
        }

        private static bool IsMeaningful(JsonElement value) => value.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined or JsonValueKind.False => false,
            JsonValueKind.String => !string.IsNullOrWhiteSpace(value.GetString()),
            JsonValueKind.Array => value.GetArrayLength() > 0,
            JsonValueKind.Object => value.EnumerateObject().Any(),
            _ => true
        };

        // Аналіз помилки 400
        private static string AnalyzeBadRequest(string content, string finalUrl, string originalUrl, MediaSource? media)
        {
            if (!string.IsNullOrWhiteSpace(media?.ApiUrl))
            {
                try
                {
                    var json = JsonSerializer.Deserialize<JsonElement>(content);
                    if (json.ValueKind == JsonValueKind.Object && json.TryGetProperty("description", out var descProp) && descProp.ValueKind == JsonValueKind.String)
                    {
                        return FormatResult($"❌ 400 (Помилковий запит): {descProp.GetString()}", media);
                    }
                }
                catch (JsonException)
                {
                    return FormatResult("❌ 400 (Помилковий запит)", media);
                }
            }

            return FormatResult("❌ 400 (Помилковий запит)", media);
        }

        // Форматування результату
        private static string FormatResult(string message, MediaSource? media)
        {
            return media != null && !string.IsNullOrEmpty(media.Name)
                ? $"{message} ({media.Name})"
                : message;
        }
    }
}
