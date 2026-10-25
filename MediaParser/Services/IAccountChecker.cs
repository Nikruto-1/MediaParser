using MediaParser.Models;
using MediaParser.Models.DBModels;

namespace MediaParser.Services;

public interface IAccountChecker
{
    Task<AccountCheckResult> CheckAsync(MediaSource source, string identifier, CancellationToken cancellationToken);

    Task<AccountCheckResult> CheckInteractiveAsync(MediaSource source, string identifier, CancellationToken cancellationToken) =>
        Task.FromResult(new AccountCheckResult(AccountStatus.Error, "Ручна перевірка недоступна в цьому середовищі."));
}
