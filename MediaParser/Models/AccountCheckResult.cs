namespace MediaParser.Models;

public enum AccountStatus
{
    Found,
    NotFound,
    Blocked,
    Unconfirmed,
    Error,
    Cancelled,
    Pending,
    Checking
}

public sealed record AccountCheckResult(AccountStatus Status, string Reason, string? Evidence = null,
    IReadOnlyList<DeclarationSummary>? Declarations = null, int? DeclarationTotal = null)
{
    public string StatusCode => Status.ToString().ToLowerInvariant();
    public string Label => Status switch
    {
        AccountStatus.Found => Declarations?.Count > 0 ? "Знайдено декларації" : "Знайдено",
        AccountStatus.NotFound => "Не знайдено",
        AccountStatus.Blocked => "Доступ обмежено",
        AccountStatus.Unconfirmed => "Не підтверджено",
        AccountStatus.Cancelled => "Скасовано",
        AccountStatus.Pending => "Очікує перевірки",
        AccountStatus.Checking => "Перевіряємо",
        _ => "Помилка перевірки"
    };

    public string ToLegacyMessage() => $"{(Status == AccountStatus.Found ? "✅" : Status == AccountStatus.NotFound ? "❌" : "⚠️")} {Label}: {Reason}";
}
