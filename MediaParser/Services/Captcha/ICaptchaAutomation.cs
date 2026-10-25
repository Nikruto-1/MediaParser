using OpenQA.Selenium;

namespace MediaParser.Services.Captcha;

public sealed record CaptchaAttemptResult(bool Attempted, bool TokenApplied, string? Message = null);

public interface ICaptchaAutomation
{
    Task<CaptchaAttemptResult> TrySolveAsync(IWebDriver driver, CaptchaSolveBudget budget,
        CancellationToken cancellationToken, Action<string>? onProgress = null);
}
