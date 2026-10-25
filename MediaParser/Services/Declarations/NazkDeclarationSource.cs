using MediaParser.Models.DBModels;

namespace MediaParser.Services.Declarations;

public static class NazkDeclarationSource
{
    public const string Name = "НАЗК — декларації";
    public const string ApiTemplate = "https://public-api.nazk.gov.ua/v2/documents/list?query={0}&full_search=0&page=1";
    public const string SearchTemplate = "https://public.nazk.gov.ua/documents/list?query={0}";

    public static bool IsSource(MediaSource source) => source.LinkType == "FullName" &&
        (IsOfficialEndpoint(source.ApiUrl, "public-api.nazk.gov.ua", "/v2/documents/list") ||
         (string.IsNullOrWhiteSpace(source.ApiUrl) && IsOfficialEndpoint(source.SearchUrl, "public.nazk.gov.ua", "/documents/list")));

    public static string SearchUrl(string fullName) => SearchTemplate.Replace("{0}", Uri.EscapeDataString(fullName));

    private static bool IsOfficialEndpoint(string? value, string host, string path) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps &&
        uri.IsDefaultPort && uri.UserInfo.Length == 0 && uri.Host.Equals(host, StringComparison.OrdinalIgnoreCase) &&
        uri.AbsolutePath.TrimEnd('/').Equals(path, StringComparison.Ordinal) && uri.Fragment.Length == 0;
}
