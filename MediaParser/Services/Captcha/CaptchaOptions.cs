namespace MediaParser.Services.Captcha;

public sealed class CaptchaOptions
{
    public const string SectionName = "Captcha";
    public bool Enabled { get; set; } = false;
    public string ApiKey { get; set; } = string.Empty;
    public int MaxTasksPerSearch { get; set; } = 3;
    public int TimeoutSeconds { get; set; } = 120;
    public int PollIntervalSeconds { get; set; } = 5;
}
