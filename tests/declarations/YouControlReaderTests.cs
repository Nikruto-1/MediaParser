using System.Reflection;
using System.Text.Json;
using MediaParser.Models;
using MediaParser.Services.Declarations;
using OpenQA.Selenium;

public static class YouControlReaderTests
{
    private const string PersonUrl = "https://youcontrol.com.ua/catalog/individuals/testovyy-korystuvach-petrovych/";
    private const string Name = "Тестовий Користувач Петрович";
    private const string PersonHtml = """
        <html><title>Публічний каталог</title><input type="password" hidden>
        <h2>Суб'єкти декларування — Тестовий Користувач Петрович<div class="label label-info">2</div></h2>
        <table class="table-yc table-declaration-subj"><tr><th>Тип декларації</th><th>Подана</th><th>Місце роботи/посада</th><th>Місто, селище чи село</th><th></th></tr>
        <tr><td><div class="td-title">Тип декларації</div>Річна</td><td><div class="td-title">Подана</div>24.03.2026</td><td><div class="td-title">Місце роботи/посада</div>Тестова установа &amp; філія / Головний спеціаліст</td><td>PRIVATE-LOCATION</td><td><a href="/catalog/individuals/declaration/11111111-1111-4111-8111-111111111111/">ДЕТАЛЬНІШЕ</a></td></tr>
        <tr><td>Перед звільненням</td><td>17.12.2020</td><td>Інша установа / Фахівець</td><td>PRIVATE-FAMILY</td><td><a href="/catalog/individuals/declaration/22222222-2222-4222-8222-222222222222/">ДЕТАЛЬНІШЕ</a></td></tr></table>
        <section>PRIVATE-SALARY</section></html>
        """;
    private static string SearchHtml => $"<html><h1>Пошук суб'єкта декларування за запитом {Name}</h1><a href='{PersonUrl}'>{Name}</a></html>";

    public static Task ParserMetadata()
    {
        var result = YouControlCatalogParser.ParsePerson(PersonHtml, PersonUrl, Name);
        Assert(result.Recognized && !result.Blocked && result.Declarations.Count == 2 && result.Total == 2, "Catalog table structure was not recognized.");
        var item = result.Declarations[0];
        Assert(item.FullName == Name && item.Workplace == "Тестова установа & філія" && item.Position == "Головний спеціаліст" &&
            item.DeclarationYear == null && item.SubmittedAt == null && item.SubmittedDate == "2026-03-24" && item.DeclarationType == "Річна",
            "Metadata projection copied labels, invented a year or precise time, or lost fields.");
        Assert(!JsonSerializer.Serialize(result).Contains("PRIVATE-"), "Private columns entered the result.");
        Assert(YouControlCatalogParser.ParsePerson(PersonHtml.Replace("<th>", "<td>").Replace("</th>", "</td>"), PersonUrl, Name).Declarations.Count == 2,
            "Equivalent td headers were rejected.");
        Assert(!YouControlCatalogParser.ParsePerson(PersonHtml, PersonUrl, "Інший Користувач Петрович").Recognized, "A person heading mismatch was accepted.");
        foreach (var bad in new[] { "https://attacker.test/catalog/individuals/test-person/", PersonUrl + "?token=secret", PersonUrl + "#fragment",
            "https://user:pass@youcontrol.com.ua/catalog/individuals/test-person/", "https://youcontrol.com.ua:444/catalog/individuals/test-person/" })
            Assert(!YouControlCatalogParser.TryPersonUrl(bad, out _), "An unsafe person URL was accepted.");
        foreach (var bad in new[] { "https://attacker.test/catalog/individuals/declaration/11111111-1111-4111-8111-111111111111/",
            "/catalog/individuals/declaration/00000000-0000-0000-0000-000000000000/", "/catalog/individuals/declaration/11111111-1111-4111-8111-111111111111/?token=x" })
            Assert(!YouControlCatalogParser.TryDocumentUrl(bad, out _, out _), "An unsafe document URL was accepted.");
        return Task.CompletedTask;
    }

    public static Task SearchAndBarriers()
    {
        var url = YouControlDeclarationSource.SearchUrl(Name);
        var found = YouControlCatalogParser.ParseSearch(SearchHtml, url, Name);
        Assert(found.Recognized && found.People.Count == 1 && found.People[0].Url == PersonUrl, "Search did not yield the exact person candidate.");
        Assert(!YouControlCatalogParser.MatchesQuery(Name + " Олександрович", Name) && !YouControlCatalogParser.MatchesQuery("Інший Користувач Петрович", Name),
            "A full-name substring was treated as an exact candidate.");
        Assert(YouControlCatalogParser.MatchesQuery(Name, "Тестовий Користувач"), "A two-part name could not request candidate verification.");
        var empty = YouControlCatalogParser.ParseSearch("<div><h1>Пошук суб'єкта декларування</h1>Всього декларантів 0</div>", url, Name);
        Assert(empty.Recognized && empty.Empty && empty.People.Count == 0, "An explicit empty search was not recognized.");
        Assert(YouControlCatalogParser.ParsePerson("<title>Трохи зачекайте…</title><form id='challenge-form'></form>", PersonUrl, Name).Blocked,
            "An access challenge became an empty profile.");
        Assert(YouControlCatalogParser.ParseSearch("<title>Вхід</title>", "https://youcontrol.com.ua/sign_in/", Name).Blocked, "A login redirect was not restricted.");
        return Task.CompletedTask;
    }

    public static async Task BrowserReading()
    {
        var browser = CatalogDriver.Create();
        var reader = new YouControlDeclarationSearch(visible => { Assert(!visible, "Automatic lookup opened visible Chrome."); return browser.Driver; });
        var result = await reader.SearchAsync(Name, CancellationToken.None);
        Assert(result.Status == AccountStatus.Found && result.Declarations?.Count == 2 && result.DeclarationTotal == 2 && browser.Visited.Count == 2 &&
            browser.Visited.All(url => !url.Contains("/declaration/")) && browser.QuitCount == 1 && browser.DisposeCount == 1,
            "Reader did not extract public rows, fetched full declarations, or leaked Chrome.");
        var manual = CatalogDriver.Create();
        var manualReader = new YouControlDeclarationSearch(visible => { Assert(visible, "Manual lookup did not request visible Chrome."); return manual.Driver; });
        Assert((await manualReader.SearchAsync(PersonUrl, CancellationToken.None, true)).Status == AccountStatus.Found && manual.Visited.Count == 1,
            "A direct person URL was sent to name search or could not be read manually.");
        using var cancelled = new CancellationTokenSource();
        var late = CatalogDriver.Create(); late.BeforeRead = () => cancelled.Cancel();
        Assert((await new YouControlDeclarationSearch(_ => late.Driver).SearchAsync(PersonUrl, cancelled.Token)).Status == AccountStatus.Cancelled && late.QuitCount == 1,
            "Cancellation during PageSource returned Found or leaked Chrome.");
        var invalid = new YouControlDeclarationSearch(_ => throw new InvalidOperationException("Invalid input must not create Chrome"));
        Assert((await invalid.SearchAsync("one", CancellationToken.None)).Status == AccountStatus.Unconfirmed, "Insufficient input reached Chrome.");
    }

    public static async Task SavedPageImport()
    {
        static MemoryStream Page(string value) => new(System.Text.Encoding.UTF8.GetBytes(value));
        var parsed = await YouControlPageImport.ReadAsync(Page(PersonHtml), PersonUrl, CancellationToken.None);
        Assert(parsed.Declarations.Count == 2 && !JsonSerializer.Serialize(parsed).Contains("PRIVATE-"), "Import lost public data or retained private columns.");
        foreach (var url in new[] { "https://attacker.test/catalog/individuals/test-person/", "/catalog/individuals/test-person/", PersonUrl + "?token=secret" })
        {
            try { await YouControlPageImport.ReadAsync(Page(PersonHtml), url, CancellationToken.None); throw new Exception("Unsafe import URL was accepted."); }
            catch (InvalidDataException) { }
        }
        foreach (var html in new[] { "<title>Трохи зачекайте</title>", "<html>unknown</html>", new string('x', YouControlPageImport.MaxBytes + 1) })
        {
            try { await YouControlPageImport.ReadAsync(Page(html), PersonUrl, CancellationToken.None); throw new Exception("Invalid/oversized saved page was accepted."); }
            catch (InvalidDataException) { }
        }
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        try { await YouControlPageImport.ReadAsync(Page(PersonHtml), PersonUrl, cancelled.Token); throw new Exception("Cancelled import read the file."); }
        catch (OperationCanceledException) { }
    }
    private static void Assert(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    public class CatalogDriver : DispatchProxy
    {
        public IWebDriver Driver { get; private set; } = null!;
        public List<string> Visited { get; } = new();
        public string CurrentUrl { get; set; } = "about:blank";
        public Action? BeforeRead { get; set; }
        public int QuitCount { get; private set; }
        public int DisposeCount { get; private set; }
        public static CatalogDriver Create()
        { var driver = DispatchProxy.Create<IWebDriver, CatalogDriver>(); var proxy = (CatalogDriver)driver; proxy.Driver = driver; return proxy; }
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            switch (method?.Name)
            {
                case "get_Url": return CurrentUrl;
                case "get_PageSource": BeforeRead?.Invoke(); return CurrentUrl.Contains("/search/") ? SearchHtml : PersonHtml;
                case "Quit": QuitCount++; return null;
                case "Dispose": DisposeCount++; return null;
                case "Navigate":
                    var navigation = DispatchProxy.Create<INavigation, CatalogNavigation>(); ((CatalogNavigation)navigation).Owner = this; return navigation;
                default: throw new NotSupportedException(method?.Name);
            }
        }
    }
    public class CatalogNavigation : DispatchProxy
    {
        public CatalogDriver Owner { get; set; } = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            if (method?.Name != "GoToUrl") throw new NotSupportedException(method?.Name);
            Owner.CurrentUrl = args![0]!.ToString()!; Owner.Visited.Add(Owner.CurrentUrl); return null;
        }
    }
}
