using MediaParser.Services.Declarations;

namespace MediaParser.Classes;

public static class SearchIdentifierLimits
{
    public const int PlainTextMaxLength = 100;
    public const int YouControlUrlMaxLength = 512;

    public static bool IsValid(string? value, bool allowYouControlUrl = true) =>
        !string.IsNullOrWhiteSpace(value) && (value.Length <= PlainTextMaxLength ||
            allowYouControlUrl && value.Length <= YouControlUrlMaxLength &&
            Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps &&
            YouControlCatalogParser.TryPersonUrl(value, out _));
}
