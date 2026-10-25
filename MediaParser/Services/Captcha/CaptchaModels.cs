namespace MediaParser.Services.Captcha;

public enum CaptchaKind { RecaptchaV2, Turnstile }

public sealed record CaptchaChallenge(CaptchaKind Kind, string WebsiteUrl, string SiteKey,
    string? Callback = null, bool Invisible = false, string? Action = null, string? Data = null);

public sealed record CaptchaSolution(bool Success, string? Token, string? ErrorCode, string Message);

/// <summary>A search owns one shared budget; failed reservations never increment it.</summary>
public sealed class CaptchaSolveBudget
{
    private int used;
    public int MaxTasks { get; }
    public int Used => Volatile.Read(ref used);

    public CaptchaSolveBudget(int maxTasks) => MaxTasks = Math.Max(0, maxTasks);

    public bool TryReserve()
    {
        while (true)
        {
            int current = Volatile.Read(ref used);
            if (current >= MaxTasks) return false;
            if (Interlocked.CompareExchange(ref used, current + 1, current) == current) return true;
        }
    }
}
