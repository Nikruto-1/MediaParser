using MediaParser.Models.DBModels;

namespace MediaParser.Services.Declarations;

/// <summary>Recognizes the public declaration catalog without trusting an editable display name.</summary>
public static class YouControlDeclarationSource
{
    public const string Name = "YouControl — декларації";
    public const string SearchTemplate = "https://youcontrol.com.ua/catalog/individuals/search/?q={0}";

    public static bool IsSource(MediaSource source) => source.LinkType == "FullName" &&
        string.IsNullOrWhiteSpace(source.ApiUrl) && IsSearchUrl(source.SearchUrl);

    public static bool IsSearchUrl(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort && uri.UserInfo.Length == 0 &&
        uri.Host.Equals("youcontrol.com.ua", StringComparison.OrdinalIgnoreCase) &&
        uri.AbsolutePath == "/catalog/individuals/search/" && uri.Fragment.Length == 0;

    public static string SearchUrl(string fullName) => SearchTemplate.Replace("{0}", Uri.EscapeDataString(fullName.Trim()));

}
