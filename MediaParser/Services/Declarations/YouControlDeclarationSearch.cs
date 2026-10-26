using System.Diagnostics;
using MediaParser.Classes;
using MediaParser.Models;
using OpenQA.Selenium;

namespace MediaParser.Services.Declarations;

public sealed class YouControlDeclarationSearch(Func<bool, IWebDriver>? driverFactory = null) : IYouControlDeclarationSearch
{
    private readonly Func<bool, IWebDriver> _driverFactory = driverFactory ?? (visible => SearchAlgorithm.CreateChromeDriver("PublicDeclarations", visible));

    public async Task<AccountCheckResult> SearchAsync(string fullName, CancellationToken cancellationToken, bool interactive = false)
    {
        if (cancellationToken.IsCancellationRequested) return Cancelled();
        fullName ??= string.Empty;
        var direct = YouControlCatalogParser.TryPersonUrl(fullName, out var directUrl);
        var query = string.Join(' ', fullName.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (!direct && (query.Length is < 3 or > 100 || !YouControlCatalogParser.MatchesQuery(query, query)))
            return new(AccountStatus.Unconfirmed, "Введіть прізвище та ім’я, бажано також по батькові, або URL відкритої картки YouControl.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(interactive ? 120 : 45));
        try
        {
            return await Task.Run(async () =>
            {
                IWebDriver? driver = null;
                try
                {
                    deadline.Token.ThrowIfCancellationRequested();
                    driver = _driverFactory(interactive);
                    deadline.Token.ThrowIfCancellationRequested();
                    IReadOnlyList<CatalogPerson> people;
                    if (direct) people = [new(string.Empty, directUrl)];
                    else
                    {
                        var search = await ReadPage(driver, YouControlDeclarationSource.SearchUrl(query),
                            (html, url) => YouControlCatalogParser.ParseSearch(html, url, query).Recognized, interactive, deadline.Token);
                        var candidates = YouControlCatalogParser.ParseSearch(search.Html, search.Url, query);
                        if (candidates.Blocked) return Blocked();
                        if (!candidates.Recognized) return Unknown();
                        if (candidates.People.Count == 0)
                            return new(AccountStatus.Unconfirmed, "У доступному пошуку YouControl не знайдено точного збігу за введеним ПІБ. Уточніть ім’я; це не доводить відсутність декларацій.",
                                "Перевірено публічний список кандидатів.", candidates.Empty ? Array.Empty<DeclarationSummary>() : null, candidates.Empty ? 0 : null);
                        people = candidates.People;
                    }
                    var records = new List<DeclarationSummary>();
                    int? total = 0;
                    bool incomplete = false;
                    foreach (var person in people)
                    {
                        deadline.Token.ThrowIfCancellationRequested();
                        var page = await ReadPage(driver, person.Url,
                            (html, url) => YouControlCatalogParser.ParsePerson(html, url, direct ? null : person.FullName).Recognized,
                            interactive, deadline.Token);
                        var parsed = YouControlCatalogParser.ParsePerson(page.Html, page.Url, direct ? null : person.FullName);
                        if (!parsed.Recognized)
                        {
                            if (records.Count == 0) return parsed.Blocked ? Blocked() : Unknown();
                            incomplete = true; total = null; break;
                        }
                        if (parsed.Total.HasValue && total.HasValue) total += parsed.Total.Value;
                        else total = null;
                        foreach (var record in parsed.Declarations)
                            if (records.All(item => item.Id != record.Id)) records.Add(record);
                        if (records.Count >= 20) { incomplete = people.Count > 1; break; }
                    }
                    if (records.Count == 0) return Unknown();
                    deadline.Token.ThrowIfCancellationRequested();
                    if (people.Count > 1) total = null;
                    return new(AccountStatus.Found,
                        "Прочитано публічні службові метадані та список декларацій із каталогу YouControl. Збіг ПІБ може стосуватися тезок; посада наведена за документом.",
                        "Отримано з публічних карток у Chrome, без завантаження повних декларацій."
                            + (incomplete ? " Частину списку не прочитано." : "")
                            + " Прочитано поточну сторінку не більш як 3 карток людей, показано до 20 документів."
                            + (people.Count > 1 ? " Загальна кількість для кількох кандидатів невідома." : "")
                            + " Рік звітності не виводиться з дати подання.",
                        records.Take(20).ToArray(), total >= records.Take(20).Count() ? total : null);
                }
                finally
                {
                    if (driver != null)
                    {
                        try { driver.Quit(); } catch (WebDriverException) { }
                        try { driver.Dispose(); } catch (WebDriverException) { }
                    }
                }
            }, deadline.Token);
        }
        catch (OperationCanceledException)
        { return cancellationToken.IsCancellationRequested ? Cancelled() : new(AccountStatus.Unconfirmed, "Час читання каталогу YouControl минув. Можна повторити або виконати ручну перевірку у Chrome."); }
        catch (WebDriverException)
        { return new(AccountStatus.Error, "Не вдалося прочитати каталог у Chrome. Перевірте наявність Chrome та повторіть спробу."); }
    }

    private static async Task<(string Html, string Url)> ReadPage(IWebDriver driver, string url,
        Func<string, string, bool> ready, bool interactive, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try { driver.Navigate().GoToUrl(url); }
        catch (WebDriverTimeoutException) { cancellationToken.ThrowIfCancellationRequested(); }
        var elapsed = Stopwatch.StartNew();
        (string Html, string Url) page;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            page = (driver.PageSource, driver.Url);
            cancellationToken.ThrowIfCancellationRequested();
            if (page.Html.Length > 2 * 1024 * 1024) return (string.Empty, page.Url);
            if (ready(page.Html, page.Url)) return page;
            if (elapsed.Elapsed >= TimeSpan.FromSeconds(interactive ? 110 : 10)) return page;
            await Task.Delay(400, cancellationToken);
        } while (true);
    }

    private static AccountCheckResult Cancelled() => new(AccountStatus.Cancelled, "Читання каталогу скасовано.");
    private static AccountCheckResult Blocked() => new(AccountStatus.Blocked,
        "YouControl обмежив доступ або очікує перевірку безпеки. Відкрийте ручну перевірку у Chrome; після вашого підтвердження застосунок зчитає доступні метадані.");
    private static AccountCheckResult Unknown() => new(AccountStatus.Unconfirmed,
        "Публічна сторінка не містить очікуваного списку декларацій або ПІБ не збігається. Дані не підтверджено; можна повторити ручну перевірку.");
}
