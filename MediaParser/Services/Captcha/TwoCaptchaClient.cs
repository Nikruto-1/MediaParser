using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace MediaParser.Services.Captcha;

/// <summary>
/// Opt-in 2Captcha API v2 client. It never retries createTask: a lost response may still represent a paid task.
/// The DI HttpClient must have redirects and automatic POST retries disabled.
/// </summary>
public sealed class TwoCaptchaClient(HttpClient httpClient, IOptions<CaptchaOptions> options) : ICaptchaSolver
{
    private static readonly Uri ApiBase = new("https://api.2captcha.com/", UriKind.Absolute);
    private static readonly HashSet<string> SensitiveQueryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "token", "access_token", "id_token", "code", "api_key", "apikey", "key", "password", "secret", "session", "auth", "signature"
    };
    private readonly CaptchaOptions settings = options.Value;
    public bool IsAvailable => settings.Enabled && !string.IsNullOrWhiteSpace(settings.ApiKey);

    public async Task<CaptchaSolution> SolveAsync(CaptchaChallenge challenge, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested) return Cancelled();
        if (!settings.Enabled) return Failure("DISABLED", "Автоматичне розв’язування CAPTCHA вимкнено.");
        if (string.IsNullOrWhiteSpace(settings.ApiKey)) return Failure("MISSING_API_KEY", "Для 2Captcha не налаштовано API-ключ.");
        if (challenge == null || !Uri.TryCreate(challenge.WebsiteUrl, UriKind.Absolute, out var website) ||
            website.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(website.UserInfo) || !string.IsNullOrEmpty(website.Fragment) ||
            HasSensitiveQuery(website) || string.IsNullOrWhiteSpace(challenge.SiteKey))
            return Failure("INVALID_CHALLENGE", "Не вдалося визначити коректну адресу сторінки або sitekey CAPTCHA.");
        if (challenge.Kind is not (CaptchaKind.RecaptchaV2 or CaptchaKind.Turnstile))
            return Failure("UNSUPPORTED_CAPTCHA", "Цей тип CAPTCHA не підтримується автоматичною перевіркою.");
        string clientKey = settings.ApiKey.Trim();

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(settings.TimeoutSeconds, 1, 600)));
        var pollInterval = TimeSpan.FromSeconds(Math.Clamp(settings.PollIntervalSeconds, 5, 60));
        try
        {
            var task = new Dictionary<string, object>
            {
                ["type"] = challenge.Kind == CaptchaKind.RecaptchaV2 ? "RecaptchaV2TaskProxyless" : "TurnstileTaskProxyless",
                ["websiteURL"] = challenge.WebsiteUrl,
                ["websiteKey"] = challenge.SiteKey
            };
            if (challenge.Kind == CaptchaKind.RecaptchaV2)
            {
                task["isInvisible"] = challenge.Invisible;
                if (!string.IsNullOrWhiteSpace(challenge.Data)) task["recaptchaDataSValue"] = challenge.Data;
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(challenge.Action)) task["action"] = challenge.Action;
                if (!string.IsNullOrWhiteSpace(challenge.Data)) task["data"] = challenge.Data;
            }

            // Callback is a page JavaScript callback, not a provider webhook URL. It is never transmitted.
            // Absolute fixed endpoints deliberately ignore HttpClient.BaseAddress and any website URL.
            using var created = await PostAsync("createTask", new { clientKey, task }, deadline.Token);
            if (created.Error != null) return created.Error;
            var create = created.Document!.RootElement;
            var error = ProviderError(create);
            if (error != null) return error;
            if (!create.TryGetProperty("taskId", out var id) || id.ValueKind != JsonValueKind.Number || !id.TryGetInt64(out long taskId) || taskId <= 0)
                return InvalidResponse();

            while (true)
            {
                // Wait before the first poll too; never send processing polls faster than five seconds.
                await Task.Delay(pollInterval, deadline.Token);
                using var polled = await PostAsync("getTaskResult", new { clientKey, taskId }, deadline.Token);
                if (polled.Error != null) return polled.Error;
                var result = polled.Document!.RootElement;
                error = ProviderError(result);
                if (error != null) return error;
                string? status = Text(result, "status");
                if (status == "processing") continue;
                if (status != "ready" || !result.TryGetProperty("solution", out var solution) || solution.ValueKind != JsonValueKind.Object)
                    return InvalidResponse();
                string? token = challenge.Kind == CaptchaKind.RecaptchaV2 ? Text(solution, "gRecaptchaResponse") : null;
                if (string.IsNullOrWhiteSpace(token)) token = Text(solution, "token");
                if (string.IsNullOrWhiteSpace(token)) return InvalidResponse();
                return new(true, token, null, "2Captcha повернув токен для перевірки на сторінці.");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return Cancelled(); }
        catch (OperationCanceledException)
        {
            return Failure("TIMEOUT", "Час очікування 2Captcha минув. Нову платну задачу автоматично не створено; попередня задача могла залишитися в сервісі.");
        }
        catch (HttpRequestException)
        {
            return Failure("NETWORK_ERROR", "Немає з’єднання з 2Captcha. Запит автоматично не повторюється, щоб не створити зайву платну задачу.");
        }
        catch (IOException) { return Failure("NETWORK_ERROR", "Не вдалося прочитати відповідь 2Captcha. Запит автоматично не повторюється."); }
        catch (JsonException) { return InvalidResponse(); }
        catch (Exception) { return Failure("CLIENT_ERROR", "Не вдалося завершити запит до 2Captcha. Запит автоматично не повторюється."); }
    }

    private async Task<ApiResponse> PostAsync(string method, object payload, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(ApiBase, method)) { Content = JsonContent.Create(payload) };
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
            return new(null, Failure("HTTP_ERROR", $"Сервіс 2Captcha повернув HTTP {(int)response.StatusCode}. Запит автоматично не повторюється."));
        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            document.Dispose();
            return new(null, InvalidResponse());
        }
        return new(document, null);
    }

    private static CaptchaSolution? ProviderError(JsonElement response)
    {
        if (!response.TryGetProperty("errorId", out var error) || error.ValueKind != JsonValueKind.Number || !error.TryGetInt32(out int id) || id < 0)
            return InvalidResponse();
        if (id == 0) return null;

        // Descriptions and unknown codes may contain request data, including credentials. Never echo them.
        return Text(response, "errorCode") switch
        {
            "ERROR_KEY_DOES_NOT_EXIST" or "ERROR_WRONG_USER_KEY" => Failure("ERROR_KEY_DOES_NOT_EXIST", "API-ключ 2Captcha недійсний. Перевірте налаштування ключа."),
            "ERROR_ZERO_BALANCE" => Failure("ERROR_ZERO_BALANCE", "На рахунку 2Captcha недостатньо коштів. Перевірте баланс сервісу."),
            "ERROR_IP_NOT_ALLOWED" => Failure("ERROR_IP_NOT_ALLOWED", "2Captcha не дозволяє запити з цієї IP-адреси. Перевірте список дозволених адрес у сервісі."),
            "ERROR_IP_BLOCKED" or "ERROR_ACCOUNT_SUSPENDED" => Failure("ERROR_ACCOUNT_SUSPENDED", "Доступ до 2Captcha заблоковано сервісом. Перевірте стан облікового запису в 2Captcha."),
            "ERROR_NO_SLOT_AVAILABLE" => Failure("ERROR_NO_SLOT_AVAILABLE", "2Captcha тимчасово не приймає нові задачі. Автоматичного повтору платного запиту не буде."),
            "ERROR_TASK_NOT_SUPPORTED" or "ERROR_TASK_ABSENT" => Failure("ERROR_TASK_NOT_SUPPORTED", "2Captcha не підтримує переданий тип або параметри CAPTCHA."),
            "ERROR_RECAPTCHA_INVALID_SITEKEY" or "ERROR_RECAPTCHA_INVALID_DOMAIN" or "ERROR_PAGEURL" or "ERROR_BAD_PARAMETERS" => Failure("INVALID_CHALLENGE", "2Captcha відхилив адресу сторінки або параметри CAPTCHA."),
            "ERROR_CAPTCHA_UNSOLVABLE" => Failure("ERROR_CAPTCHA_UNSOLVABLE", "2Captcha не зміг розв’язати цю CAPTCHA. Перевірте її вручну."),
            _ => Failure("PROVIDER_ERROR", "2Captcha відхилив задачу. Перевірте налаштування та стан облікового запису сервісу.")
        };
    }

    private static string? Text(JsonElement value, string property) => value.TryGetProperty(property, out var field) && field.ValueKind == JsonValueKind.String ? field.GetString() : null;
    private static bool HasSensitiveQuery(Uri website) => website.Query.TrimStart('?').Split(new[] { '&', ';' }, StringSplitOptions.RemoveEmptyEntries)
        .Select(part => System.Net.WebUtility.UrlDecode(part.Split('=', 2)[0]).Trim().Split('[', 2)[0])
        .Any(SensitiveQueryNames.Contains);
    private static CaptchaSolution Failure(string code, string message) => new(false, null, code, message);
    private static CaptchaSolution Cancelled() => Failure("CANCELLED", "Очікування 2Captcha скасовано. Уже створену задачу сервіс може обробити окремо.");
    private static CaptchaSolution InvalidResponse() => Failure("INVALID_RESPONSE", "2Captcha повернув неочікувану відповідь. Запит автоматично не повторюється.");
    private sealed record ApiResponse(JsonDocument? Document, CaptchaSolution? Error) : IDisposable
    {
        public void Dispose() => Document?.Dispose();
    }
}
