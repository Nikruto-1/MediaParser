using MediaParser.Models;
using MediaParser.Models.DBModels;
using MediaParser.Services.Captcha;

namespace MediaParser.Services;

public interface IAccountChecker
{
    Task<AccountCheckResult> CheckAsync(MediaSource source, string identifier, CancellationToken cancellationToken);

    Task<AccountCheckResult> CheckWithCaptchaAsync(MediaSource source, string identifier, CaptchaSolveBudget budget,
        Action<string> onProgress, CancellationToken cancellationToken) => CheckAsync(source, identifier, cancellationToken);

    Task<AccountCheckResult> CheckInteractiveAsync(MediaSource source, string identifier, CancellationToken cancellationToken) =>
        Task.FromResult(new AccountCheckResult(AccountStatus.Error, "Ручна перевірка недоступна в цьому середовищі."));
}
