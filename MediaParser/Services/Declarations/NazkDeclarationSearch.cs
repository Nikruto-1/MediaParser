using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using MediaParser.Models;

namespace MediaParser.Services.Declarations;

/// <summary>Reads only the first public search page, never the contents of individual declarations.</summary>
public sealed class NazkDeclarationSearch(HttpClient client) : INazkDeclarationSearch
{
    private const int MaximumResponseBytes = 2 * 1024 * 1024;
    private const int MaximumResults = 20;

    public async Task<AccountCheckResult> SearchAsync(string fullName, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested) return new(AccountStatus.Cancelled, "Пошук декларацій скасовано.");
        var query = string.Join(' ', (fullName ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (query.Length is < 3 or > 100 || query.Any(char.IsControl))
            return new(AccountStatus.Unconfirmed, "Для пошуку декларацій введіть ПІБ довжиною від 3 до 100 символів.");

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            // The host and search scope are fixed. Source settings and API keys cannot change this request.
            using var request = new HttpRequestMessage(HttpMethod.Get,
                NazkDeclarationSource.ApiTemplate.Replace("{0}", Uri.EscapeDataString(query)));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.UserAgent.ParseAdd("MediaParser/1.0 (public declaration search)");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
                return new(AccountStatus.Blocked, "НАЗК тимчасово обмежує доступ до публічного API. Спробуйте пізніше або відкрийте офіційний реєстр.");
            if (!response.IsSuccessStatusCode)
                return new(AccountStatus.Error, "Публічний API НАЗК зараз не повертає результатів пошуку. Спробуйте пізніше.");
            if (response.Content.Headers.ContentLength > MaximumResponseBytes)
                return InvalidResponse("Відповідь реєстру перевищує обмеження розміру.");
            await using var body = await response.Content.ReadAsStreamAsync(deadline.Token);
            using var buffer = new MemoryStream();
            var chunk = new byte[16 * 1024];
            int read;
            while ((read = await body.ReadAsync(chunk, deadline.Token)) > 0)
            {
                if (buffer.Length + read > MaximumResponseBytes)
                    return InvalidResponse("Відповідь реєстру перевищує обмеження розміру.");
                buffer.Write(chunk, 0, read);
            }
            using var json = JsonDocument.Parse(buffer.GetBuffer().AsMemory(0, (int)buffer.Length),
                new JsonDocumentOptions { MaxDepth = 32 });
            return ParseSearchPage(json.RootElement);
        }
        catch (OperationCanceledException)
        {
            return cancellationToken.IsCancellationRequested
                ? new(AccountStatus.Cancelled, "Пошук декларацій скасовано.")
                : new(AccountStatus.Error, "НАЗК не відповів за 15 секунд. Спробуйте пізніше або відкрийте офіційний реєстр.");
        }
        catch (HttpRequestException)
        {
            return new(AccountStatus.Error, "Не вдалося з’єднатися з публічним API НАЗК. Спробуйте пізніше.");
        }
        catch (IOException)
        {
            return new(AccountStatus.Error, "З’єднання з публічним API НАЗК перервано. Спробуйте пізніше.");
        }
        catch (JsonException)
        {
            return InvalidResponse();
        }
    }

    private static AccountCheckResult InvalidResponse(string? detail = null) => new(AccountStatus.Unconfirmed,
        "Не вдалося прочитати результат публічного API НАЗК. Перевірте запит в офіційному реєстрі.", detail);

    private static AccountCheckResult ParseSearchPage(JsonElement root)
    {
        // A changed schema is an unknown result, never evidence that a person has no declarations.
        if (root.ValueKind != JsonValueKind.Object) return InvalidResponse();
        if (root.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null &&
            error.ValueKind != JsonValueKind.False && error.ToString() is not ("0" or "")) return InvalidResponse();
        JsonElement items;
        if (!root.TryGetProperty("data", out items) && !root.TryGetProperty("items", out items)) return InvalidResponse();
        if (items.ValueKind != JsonValueKind.Array) return InvalidResponse();
        int? total = null;
        foreach (var key in new[] { "total", "totalItems", "count" })
            if (root.TryGetProperty(key, out var count) && int.TryParse(count.ToString(), NumberStyles.None,
                CultureInfo.InvariantCulture, out var number) && number >= items.GetArrayLength())
            { total = number; break; }
        if (items.GetArrayLength() == 0)
            return total > 0 ? InvalidResponse() : new(AccountStatus.Unconfirmed,
                "Публічних декларацій за цим запитом не знайдено. Уточніть ПІБ; це не доводить відсутність декларацій або роботи в державному органі.",
                "Пошук лише за ПІБ; частина документів може бути недоступною.", Array.Empty<DeclarationSummary>(), total ?? 0);

        var declarations = new List<DeclarationSummary>();
        var seen = new HashSet<Guid>();
        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !Guid.TryParse(Text(item, "id"), out var id) || id == Guid.Empty) continue;
            var card = item.TryGetProperty("infocard", out var info) && info.ValueKind == JsonValueKind.Object ? info : item;
            var name = NameText(card, "full_name", "fullName", "fullname");
            if (name is null)
            {
                var last = NameText(card, "last_name", "lastName", "lastname");
                var first = NameText(card, "first_name", "firstName", "firstname");
                var middle = NameText(card, "middle_name", "middleName", "middlename");
                if (last is not null && first is not null) name = string.Join(' ', new[] { last, first, middle }.Where(value => value is not null));
            }
            // A UUID without a public declarant name is insufficient evidence of a search result.
            if (name is null || !seen.Add(id)) continue;
            declarations.Add(new(id.ToString("D"), name,
                Text(item, "declaration_year") ?? Text(card, "declaration_year", "declarationYear"),
                DeclarationType(Text(item, "declaration_type") ?? Text(card, "declaration_type", "declarationType")),
                DocumentType(Text(item, "document_type") ?? Text(card, "document_type", "documentType")),
                Text(card, "position", "post"), Text(card, "work_place", "workPlace", "placeOfWork"),
                ParseDate(Text(item, "date")), "https://public.nazk.gov.ua/documents/" + id.ToString("D")));
            if (declarations.Count == MaximumResults) break;
        }
        if (declarations.Count == 0) return InvalidResponse();
        return new(AccountStatus.Found,
            "У публічному пошуку НАЗК знайдено документи за ПІБ. Серед результатів можуть бути тезки; перевірте посаду та місце роботи в офіційному реєстрі.",
            "Метадані першої сторінки публічного API; показано до 20 документів. Збіг ПІБ не підтверджує особу.",
            declarations, total);
    }

    private static string? NameText(JsonElement source, params string[] keys)
    {
        foreach (var key in keys)
            if (source.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String)
            {
                var text = Text(source, key);
                if (text?.Any(char.IsLetter) == true) return text;
            }
        return null;
    }

    private static string? Text(JsonElement source, params string[] keys)
    {
        foreach (var key in keys)
            if (source.TryGetProperty(key, out var value) && value.ValueKind is JsonValueKind.String or JsonValueKind.Number)
            {
                var text = value.ToString().Trim();
                if (text.Length is > 0 and <= 500 && !text.Any(char.IsControl)) return text;
            }
        return null;
    }

    private static string? DeclarationType(string? value) => value switch
    {
        "1" => "Щорічна", "2" => "При звільненні (перед звільненням)",
        "3" => "Щорічна (після звільнення)", "4" => "Кандидата на посаду",
        null => null, _ => int.TryParse(value, out _) ? null : value
    };

    private static string? DocumentType(string? value) => value switch
    {
        "1" => "Декларація", "2" => "Повідомлення про суттєві зміни у майновому стані", "3" => "Виправлена декларація",
        null => null, _ => int.TryParse(value, out _) ? null : value
    };

    private static DateTimeOffset? ParseDate(string? value)
    {
        if (long.TryParse(value, out var unix))
            return unix is >= 1470009600 and <= 253402300799 ? DateTimeOffset.FromUnixTimeSeconds(unix) : null;
        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date)
            && date.Year >= 2016 ? date.ToUniversalTime() : null;
    }
}
