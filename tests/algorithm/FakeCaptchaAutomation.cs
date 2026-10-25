using MediaParser.Services.Captcha;
using OpenQA.Selenium;

public sealed class FakeCaptchaAutomation(Func<CaptchaAttemptResult> action) : ICaptchaAutomation
{
    public int Calls { get; private set; }
    public Task<CaptchaAttemptResult> TrySolveAsync(IWebDriver driver, CaptchaSolveBudget budget,
        CancellationToken cancellationToken, Action<string>? onProgress = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Calls++;
        return Task.FromResult(action());
    }
}
