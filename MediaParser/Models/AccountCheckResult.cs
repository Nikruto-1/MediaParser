namespace MediaParser.Models;

public enum AccountStatus
{
    Found,
    NotFound,
    Blocked,
    Unconfirmed,
    Error,
    Cancelled
}

public sealed record AccountCheckResult(AccountStatus Status, string Reason, string? Evidence = null)
{
    public string StatusCode => Status.ToString().ToLowerInvariant();
    public string Label => Status switch
    {
        AccountStatus.Found => "Знайдено",
        AccountStatus.NotFound => "Не знайдено",
        AccountStatus.Blocked => "Доступ обмежено",
        AccountStatus.Unconfirmed => "Не підтверджено",
        AccountStatus.Cancelled => "Скасовано",
        _ => "Помилка перевірки"
    };

    public string ToLegacyMessage() => $"{(Status == AccountStatus.Found ? "✅" : Status == AccountStatus.NotFound ? "❌" : "⚠️")} {Label}: {Reason}";
}
