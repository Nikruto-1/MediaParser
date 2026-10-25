namespace MediaParser.Services.Captcha;

public interface ICaptchaSolver
{
    bool IsAvailable { get; }
    Task<CaptchaSolution> SolveAsync(CaptchaChallenge challenge, CancellationToken cancellationToken);
}
