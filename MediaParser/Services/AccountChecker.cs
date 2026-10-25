using MediaParser.Classes;
using MediaParser.Models;
using MediaParser.Models.DBModels;

namespace MediaParser.Services;

public sealed class AccountChecker : IAccountChecker
{
    public Task<AccountCheckResult> CheckInteractiveAsync(MediaSource source, string identifier, CancellationToken cancellationToken)
    {
        // A manual check opens only the public profile URL, never a credential-bearing API endpoint.
        var publicSource = new MediaSource
        {
            Id = source.Id, Name = source.Name, SearchUrl = source.SearchUrl,
            ErrorKeywordsRaw = source.ErrorKeywordsRaw, LinkType = source.LinkType
        };
        return CheckCoreAsync(publicSource, identifier, cancellationToken, interactive: true);
    }

    public async Task<AccountCheckResult> CheckAsync(MediaSource source, string identifier, CancellationToken cancellationToken)
        => await CheckCoreAsync(source, identifier, cancellationToken, interactive: false);

    private static async Task<AccountCheckResult> CheckCoreAsync(MediaSource source, string identifier, CancellationToken cancellationToken, bool interactive)
    {
        if (string.IsNullOrWhiteSpace(source.ApiUrl) && !SourceCapabilities.SupportsPublicProfileLookup(source.SearchUrl))
            return new(AccountStatus.Unconfirmed, "Це джерело не надає публічного пошуку профілю за цим URL.", SourceCapabilities.GetHint(source.SearchUrl));
        var template = !string.IsNullOrWhiteSpace(source.ApiUrl) ? source.ApiUrl : source.SearchUrl;
        if (string.IsNullOrWhiteSpace(template))
            return new(AccountStatus.Error, "У джерела немає адреси перевірки.");
        try
        {
            var url = SearchAlgorithm.BuildSearchUrl(template, identifier);
            return await SearchAlgorithm.CheckDetailedAsync(url, source.ApiKey, source, "Default",
                cancellationToken: cancellationToken, interactive: interactive);
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
