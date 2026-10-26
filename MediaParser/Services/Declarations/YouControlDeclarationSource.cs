using MediaParser.Models;
using MediaParser.Models.DBModels;

namespace MediaParser.Services.Declarations;

/// <summary>Public catalog handoff. This integration does not fetch or copy YouControl data.</summary>
public static class YouControlDeclarationSource
{
    public const string Name = "YouControl — декларації";
    public const string SearchTemplate = "https://youcontrol.com.ua/catalog/individuals/search/?q={0}";

    public static bool IsSource(MediaSource source) => source.LinkType == "FullName" &&
        string.IsNullOrWhiteSpace(source.ApiUrl) && IsSearchUrl(source.SearchUrl);

    public static bool IsSearchUrl(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort && uri.UserInfo.Length == 0 &&
        uri.Host.Equals("youcontrol.com.ua", StringComparison.OrdinalIgnoreCase) &&
        uri.AbsolutePath == "/catalog/individuals/search/" && uri.Fragment.Length == 0;

    public static string SearchUrl(string fullName) => SearchTemplate.Replace("{0}", Uri.EscapeDataString(fullName.Trim()));

    public static AccountCheckResult PrepareSearch(string fullName, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested) return new(AccountStatus.Cancelled, "Пошук скасовано.");
        if (string.IsNullOrWhiteSpace(fullName) || fullName.Trim().Length is < 3 or > 100 || fullName.Any(char.IsControl))
            return new(AccountStatus.Unconfirmed, "Введіть ПІБ довжиною від 3 до 100 символів для пошуку на YouControl.");
        return new(AccountStatus.Unconfirmed,
            "Посилання для пошуку підготовлено. Перегляньте результати за ПІБ у каталозі YouControl.",
            "Автоматичне отримання даних через ліцензійний API YouControl/YouScore не підключено. Наявність декларацій не перевірялася.");
    }
}
