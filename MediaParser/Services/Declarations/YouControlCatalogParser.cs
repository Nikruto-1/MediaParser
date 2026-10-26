using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using HtmlAgilityPack;
using MediaParser.Models;

namespace MediaParser.Services.Declarations;

public sealed record CatalogPerson(string FullName, string Url);
public sealed record CatalogSearchPage(bool Recognized, bool Blocked, IReadOnlyList<CatalogPerson> People, bool Empty);
public sealed record CatalogPersonPage(bool Recognized, bool Blocked, IReadOnlyList<DeclarationSummary> Declarations, int? Total);

/// <summary>Whitelisted public professional metadata only; no individual declaration pages are fetched.</summary>
public static class YouControlCatalogParser
{
    private const string PersonHeading = "Суб'єкти декларування —";
    private static readonly Regex PersonPath = new(@"^/catalog/individuals/[a-z0-9]+(?:-[a-z0-9]+)+/$", RegexOptions.CultureInvariant);
    private static readonly Regex DocumentPath = new(@"^/catalog/individuals/declaration/([0-9a-fA-F-]{36})/$", RegexOptions.CultureInvariant);

    public static CatalogSearchPage ParseSearch(string html, string finalUrl, string query)
    {
        if (!YouControlDeclarationSource.IsSearchUrl(finalUrl)) return new(false, IsLoginRedirect(finalUrl), [], false);
        var document = Load(html);
        if (IsBlocked(document)) return new(false, true, [], false);
        var heading = Text(document.DocumentNode.SelectSingleNode("//h1"));
        if (!heading.StartsWith("Пошук суб'єкта декларування", StringComparison.OrdinalIgnoreCase))
            return new(false, false, [], false);
        var people = new List<CatalogPerson>();
        foreach (var link in document.DocumentNode.SelectNodes("//a[@href]") ?? Enumerable.Empty<HtmlNode>())
        {
            var name = Text(link);
            if (!MatchesQuery(name, query) || !TryPersonUrl(link.GetAttributeValue("href", ""), out var url)) continue;
            if (people.All(person => person.Url != url)) people.Add(new(name, url));
            if (people.Count == 3) break;
        }
        var titleRegion = Text(document.DocumentNode.SelectSingleNode("//h1")?.ParentNode);
        return new(true, false, people, people.Count == 0 && Regex.IsMatch(titleRegion, @"Всього\s+декларантів\s+0(?:\D|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
    }

    public static CatalogPersonPage ParsePerson(string html, string finalUrl, string? expectedName)
    {
        if (!TryPersonUrl(finalUrl, out _)) return new(false, IsLoginRedirect(finalUrl), [], null);
        var document = Load(html);
        if (IsBlocked(document)) return new(false, true, [], null);
        var heading = document.DocumentNode.SelectNodes("//h2")?.FirstOrDefault(node => TextWithoutLabels(node).StartsWith(PersonHeading, StringComparison.OrdinalIgnoreCase));
        if (heading is null) return new(false, false, [], null);
        var name = TextWithoutLabels(heading)[PersonHeading.Length..].Trim();
        if (!MatchesQuery(name, name) || expectedName != null && !NormalizeName(name).Equals(NormalizeName(expectedName), StringComparison.Ordinal)) return new(false, false, [], null);
        var table = document.DocumentNode.SelectSingleNode("//table[contains(concat(' ', normalize-space(@class), ' '), ' table-declaration-subj ')]");
        if (table is null) return new(false, false, [], null);
        var header = table.SelectSingleNode(".//tr[th or td]");
        var labels = header?.SelectNodes("./th|./td")?.Select(Text).ToArray();
        if (labels is not { Length: >= 3 } || labels[0] != "Тип декларації" || labels[1] != "Подана" || labels[2] != "Місце роботи/посада")
            return new(false, false, [], null);
        int? total = int.TryParse(Text(heading.SelectSingleNode(".//div[contains(concat(' ', normalize-space(@class), ' '), ' label ')]")), out var count)
            && count >= 0 ? count : null;
        var declarations = new List<DeclarationSummary>();
        foreach (var row in table.SelectNodes(".//tr[td]") ?? Enumerable.Empty<HtmlNode>())
        {
            var cells = row.SelectNodes("./td");
            if (cells is not { Count: >= 5 }) continue;
            var link = cells[^1].SelectSingleNode(".//a[@href]");
            if (!TryDocumentUrl(link?.GetAttributeValue("href", ""), out var id, out var url)) continue;
            var type = TextWithoutLabels(cells[0]);
            var date = TextWithoutLabels(cells[1]);
            var work = TextWithoutLabels(cells[2]);
            if (type.Length is 0 or > 100 || work.Length > 1500) continue;
            string? submittedDate = DateOnly.TryParseExact(date, "dd.MM.yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
                && day.Year >= 2016 ? day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : null;
            var split = work.IndexOf(" / ", StringComparison.Ordinal);
            var workplace = split >= 0 ? work[..split].Trim() : work;
            var position = split >= 0 ? work[(split + 3)..].Trim() : null;
            if (declarations.All(item => item.Id != id))
                declarations.Add(new(id, name, null, type, null, EmptyToNull(position), EmptyToNull(workplace), null, url, submittedDate));
            if (declarations.Count == 20) break;
        }
        return new(true, false, declarations, total >= declarations.Count ? total : null);
    }

    public static bool TryPersonUrl(string? value, out string url)
    {
        url = string.Empty;
        if (!TryPublicUri(value, out var uri) || !PersonPath.IsMatch(uri!.AbsolutePath)) return false;
        url = uri.AbsoluteUri;
        return true;
    }

    public static bool TryDocumentUrl(string? value, out string id, out string url)
    {
        id = url = string.Empty;
        if (!TryPublicUri(value, out var uri)) return false;
        var match = DocumentPath.Match(uri!.AbsolutePath);
        if (!match.Success || !Guid.TryParseExact(match.Groups[1].Value, "D", out var guid) || guid == Guid.Empty) return false;
        id = guid.ToString("D");
        url = "https://youcontrol.com.ua/catalog/individuals/declaration/" + id + "/";
        return true;
    }

    public static bool MatchesQuery(string name, string query)
    {
        var parts = NormalizeName(query).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var candidate = NormalizeName(name).Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (parts.Length is < 2 or > 5 || candidate.Count < parts.Length || candidate.Count > 5) return false;
        if (parts.Any(part => !part.Any(char.IsLetter))) return false;
        if (parts.Length >= 3) return NormalizeName(name) == NormalizeName(query);
        foreach (var part in parts) if (!candidate.Remove(part)) return false;
        return true;
    }

    private static bool TryPublicUri(string? value, out Uri? uri) =>
        Uri.TryCreate(new Uri("https://youcontrol.com.ua/"), value, out uri) && uri.Scheme == Uri.UriSchemeHttps &&
        uri.Host == "youcontrol.com.ua" && uri.IsDefaultPort && uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0;
    private static string NormalizeName(string value) => string.Join(' ', value.Normalize(NormalizationForm.FormC)
        .Replace('’', '\'').Replace('ʼ', '\'').Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();
    private static HtmlDocument Load(string html) { var document = new HtmlDocument(); document.LoadHtml(html); return document; }
    private static string? EmptyToNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
    private static string Text(HtmlNode? node) => node is null ? string.Empty : string.Join(' ', HtmlEntity.DeEntitize(node.InnerText)
        .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    private static string TextWithoutLabels(HtmlNode node) => string.Join(' ', node.ChildNodes.Where(child => child.Name is not ("script" or "style") &&
        !child.GetAttributeValue("class", "").Split(' ').Any(value => value is "td-title" or "label"))
        .Select(Text).Where(value => value.Length > 0));
    private static bool IsBlocked(HtmlDocument document)
    {
        var title = Text(document.DocumentNode.SelectSingleNode("//title"));
        return title.Contains("Just a moment", StringComparison.OrdinalIgnoreCase) || title.Contains("Трохи зачекайте", StringComparison.OrdinalIgnoreCase)
            || document.DocumentNode.SelectSingleNode("//*[@id='challenge-form' or @id='cf-challenge-running']") != null;
    }
    private static bool IsLoginRedirect(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps && uri.Host == "youcontrol.com.ua" && uri.AbsolutePath == "/sign_in/";
}
