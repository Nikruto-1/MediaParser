using MediaParser.Classes;
using MediaParser.Models;
using MediaParser.Models.DBModels;

namespace MediaParser.Services;

public sealed class AccountChecker : IAccountChecker
{
    public async Task<AccountCheckResult> CheckAsync(MediaSource source, string identifier, CancellationToken cancellationToken)
    {
        var template = !string.IsNullOrWhiteSpace(source.ApiUrl) ? source.ApiUrl : source.SearchUrl;
        if (string.IsNullOrWhiteSpace(template))
            return new(AccountStatus.Error, "У джерела немає адреси перевірки.");
        try
        {
            var url = SearchAlgorithm.BuildSearchUrl(template, identifier);
            return await SearchAlgorithm.CheckDetailedAsync(url, source.ApiKey, source, "Default",
                cancellationToken: cancellationToken);
        }
        catch (ArgumentException)
        {
            return new(AccountStatus.Error, "Неправильний шаблон адреси джерела. Перевірте його налаштування.");
        }
        catch (FormatException)
        {
            return new(AccountStatus.Error, "Неправильний шаблон адреси джерела. Перевірте його налаштування.");
        }
    }
}
