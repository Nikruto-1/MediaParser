namespace MediaParser.Classes;

/// <summary>Input requirements and known limitations; these hints never change an account verdict.</summary>
public static class SourceCapabilities
{
    public static bool SupportsPublicProfileLookup(string? searchUrl)
    {
        // URL validity is checked by the caller. Unknown providers retain their normal conservative checks.
        if (!Uri.TryCreate(searchUrl, UriKind.Absolute, out var uri)) return true;
        string host = uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? uri.Host[4..] : uri.Host;
        return !host.Equals("epicgames.com", StringComparison.OrdinalIgnoreCase);
    }

    public static bool SupportsManualCheck(string? searchUrl)
    {
        if (!Uri.TryCreate(searchUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return false;
        return SupportsPublicProfileLookup(searchUrl);
    }

    public static string? GetHint(string? searchUrl)
    {
        if (!Uri.TryCreate(searchUrl, UriKind.Absolute, out var uri)) return null;
        string host = uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? uri.Host[4..] : uri.Host;
        return host.ToLowerInvariant() switch
        {
            "youcontrol.com.ua" => "YouControl: відкриття пошуку декларацій за ПІБ у каталозі. Автоматичне отримання через ліцензійний API не підключено; результати перегляньте на YouControl.",
            "public.nazk.gov.ua" => "НАЗК: пошук декларацій за ПІБ через офіційний публічний API. Збіг ПІБ не підтверджує особу; частина декларацій може бути закритою.",
            "epicgames.com" => "Epic Games: адреса /id/ не підтверджує публічний профіль за display name. Потрібна ручна перевірка через Epic Games або офіційний авторизований API.",
            "open.spotify.com" => "Spotify: введіть точний username/ID із посилання /user/…, а не відображуване ім’я профілю.",
            "steamcommunity.com" when uri.AbsolutePath.StartsWith("/id/", StringComparison.OrdinalIgnoreCase) => "Steam: тут перевіряється частина власної адреси /id/…, а не ім’я профілю або логін. SteamID64 використовує іншу адресу /profiles/….",
            "linkedin.com" when uri.AbsolutePath.StartsWith("/in/", StringComparison.OrdinalIgnoreCase) => "LinkedIn: потрібна точна частина публічної адреси після /in/…. Для пошуку за ПІБ оберіть окреме джерело LinkedIn-ПІБ.",
            _ => null
        };
    }
}
