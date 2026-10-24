using System.ComponentModel.DataAnnotations;
using System.IO.Compression;
using System.Xml.Linq;
using MediaParser.Classes;
using MediaParser.Controllers;
using MediaParser.IRepositories;
using MediaParser.Models;
using MediaParser.Models.DBModels;
using MediaParser.Repositories;
using MediaParser.Services;
using MediaParser.Services.DBServices;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

var tests = new (string Name, Func<Task> Run)[]
{
    ("Media source validates JSON arrays and HTTP URL templates", ValidateMedia),
    ("Media session refresh preserves choices across edits and membership changes", RefreshSelections),
    ("Corrupt and null session payloads recover without a crash", RecoverSession),
    ("Result history and latest export survive a session roundtrip", ResultsRoundtrip),
    ("CRUD preserves API keys and reports missing records", CrudBehavior),
    ("Excel export emits valid XML and keeps formulas literal", ExcelBehavior)
};
foreach (var test in tests)
{
    await test.Run();
    Console.WriteLine($"PASS {test.Name}");
}
Console.WriteLine($"Passed {tests.Length} media regression tests.");

static Task ValidateMedia()
{
    var source = Source(1, "GitHub");
    Assert(IsValid(source), "A correct source should validate.");
    foreach (var raw in new[] { "broken", "{}", "null", "[null]", "[1]", "[\"\"]", "[\"  \"]" })
    {
        source.ErrorKeywordsRaw = raw;
        Assert(!IsValid(source), $"Invalid keywords accepted: {raw}");
        _ = source.ErrorKeywords;
    }
    source.ErrorKeywordsRaw = "[]";
    foreach (var url in new[] { "javascript:{0}", "file:///tmp/{0}", "https://example.com/no-placeholder", "https://example.com/{1}", "https://example.com/{0}/{", @"https\://example.com/{0}" })
    {
        source.SearchUrl = url;
        Assert(!IsValid(source), $"Invalid URL accepted: {url}");
    }
    source.SearchUrl = null;
    source.ApiUrl = "https://api.example.com/users/{0}";
    Assert(IsValid(source), "An API-only source should validate.");
    source.Name = " ";
    Assert(!IsValid(source), "Whitespace names must be rejected.");
    return Task.CompletedTask;
}

static async Task RefreshSelections()
{
    var fixture = Fixture(Source(1, "GitHub"), Source(2, "Reddit"));
    Assert(fixture.Media.GetMediaSources().Count == 0, "An empty session should be empty.");
    await fixture.Media.UpdateMedia();
    fixture.Media.SetSelectedMedia(new() { "GitHub" });
    fixture.Repository.Items[0].Name = "GitHub renamed";
    fixture.Repository.Items[0].SearchUrl = "https://github.example.com/{0}";
    fixture.Repository.Items.RemoveAt(1);
    fixture.Repository.Items.Add(Source(3, "New source"));
    await fixture.Media.UpdateMediaLoyout();
    var selected = fixture.Media.GetSelectedMedia();
    Assert(selected.Count == 1 && selected[0].Id == 1 && selected[0].Name == "GitHub renamed", "The edited source lost its selection.");
    Assert(selected[0].SearchUrl == "https://github.example.com/{0}", "The cached source URL is stale.");
    Assert(fixture.Media.GetMediaSources().All(source => source.Media.Id != 2), "A removed source remained cached.");
    Assert(!fixture.Media.GetMediaSources().Single(source => source.Media.Id == 3).IsSelected, "A new source should start unselected.");
    fixture.Media.SetSelectedMedia(null);
    Assert(fixture.Media.GetSelectedMedia().Count == 0, "Clearing all choices failed.");
}

static async Task RecoverSession()
{
    var fixture = Fixture(Source(1, "GitHub"));
    foreach (var raw in new[] { "null", "[null]", "broken", "{}", "[{\"Item1\":null,\"Item2\":true}]" })
    {
        fixture.Session.SetString("UserMediaSources", raw);
        await fixture.Media.UpdateMediaLoyout();
        Assert(fixture.Media.GetMediaSources().Count == 1, "Source recovery failed.");
        fixture.Session.SetString("UserResults", raw);
        _ = new ResultsRepository(fixture.Accessor).GetListResults();
    }
}

static Task ResultsRoundtrip()
{
    var fixture = Fixture();
    var repository = new ResultsRepository(fixture.Accessor);
    repository.AddResults(new()
    {
        new() { Nickname = "first", Date = new DateTime(2026, 10, 24, 12, 30, 0), Results = new() { ["GitHub"] = ("✅ exists", "https://github.com/first") } }
    });
    repository.AddResult(new() { Nickname = "second" });
    Assert(repository.GetListResults().Count == 2, "History was lost.");
    Assert(repository.GetListResults()[0].Results["GitHub"].ProfileUrl == "https://github.com/first", "Tuple fields were lost in JSON.");
    Assert(repository.GetListLustResults().Single().Nickname == "second", "Latest results were not replaced.");
    fixture.Session.SetString("UserResults", "[{\"Nickname\":null,\"Results\":null}]");
    Assert(repository.GetListResults().Single().Results.Count == 0, "Legacy null results were not normalized.");
    repository.ClearResults();
    Assert(repository.GetListResults().Count == 0 && repository.GetListLustResults().Count == 0, "Clear left old export data behind.");
    return Task.CompletedTask;
}

static async Task CrudBehavior()
{
    var stored = Source(1, "GitHub");
    stored.ApiKey = "test-only-placeholder";
    var fixture = Fixture(stored);
    var controller = new MediaController(fixture.Media, fixture.Database);
    Assert(await controller.Update(404) is NotFoundResult, "Unknown edit should return 404.");
    var editView = (ViewResult)await controller.Update(1);
    Assert(((MediaSource)editView.Model!).ApiKey == null, "The stored credential was exposed to the view.");
    var updated = Source(1, "Updated");
    Assert(await fixture.Database.UpdateMediaAsync(updated), "Existing update failed.");
    Assert(stored.Name == "Updated" && stored.ApiKey == "test-only-placeholder", "Update replaced the key with an empty form value.");
    Assert(fixture.Repository.Items.Count == 1, "Update created a second record.");
    Assert(!await fixture.Database.UpdateMediaAsync(Source(404, "missing")), "Missing update succeeded.");
    Assert(!await fixture.Database.DeleteMediaAsync(404), "Missing delete succeeded.");
    Assert(await fixture.Database.DeleteMediaAsync(1) && fixture.Repository.Items.Count == 0, "Existing delete failed.");
}

static Task ExcelBehavior()
{
    var results = new List<ResultsModel>
    {
        new() { Nickname = "=SUM(1,2)", Results = new() { ["Media & <test>"] = ("✅ verified\u0001", "https://example.com/?a=1&b=2") } }
    };
    var bytes = ExcelExport.GenerateExcelFile(results);
    using var stream = new MemoryStream(bytes);
    using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
    foreach (var entry in archive.Entries)
    {
        using var xml = entry.Open();
        _ = XDocument.Load(xml);
    }
    using var sheet = archive.GetEntry("xl/worksheets/sheet1.xml")!.Open();
    var document = XDocument.Load(sheet);
    XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    Assert(document.Descendants(ns + "row").Count() == 2, "Export rows are missing.");
    Assert(document.Descendants(ns + "f").Count() == 0, "An identifier became an Excel formula.");
    Assert(document.Descendants(ns + "t").Any(text => text.Value == "=SUM(1,2)"), "Formula-like identifiers should remain literal text.");
    Assert(document.Descendants(ns + "t").Any(text => text.Value == "Media & <test>"), "XML escaping changed text.");
    _ = ExcelExport.GenerateExcelFile(new());
    return Task.CompletedTask;
}

static bool IsValid(MediaSource source) => Validator.TryValidateObject(source, new ValidationContext(source), new List<ValidationResult>(), true);
static MediaSource Source(int id, string name) => new() { Id = id, Name = name, SearchUrl = "https://example.com/{0}", ErrorKeywordsRaw = "[\"User not found\"]", LinkType = "User" };
static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
static (MediaService Media, MediaSourceService Database, FakeRepository Repository, MemorySession Session, HttpContextAccessor Accessor) Fixture(params MediaSource[] sources)
{
    var session = new MemorySession();
    var accessor = new HttpContextAccessor { HttpContext = new DefaultHttpContext { Session = session } };
    var repository = new FakeRepository(sources);
    var database = new MediaSourceService(new FakeUnitOfWork(repository), NullLogger<MediaSourceService>.Instance);
    return (new MediaService(database, accessor), database, repository, session, accessor);
}

sealed class FakeRepository(IEnumerable<MediaSource> items) : IMediaSourceRepository
{
    public List<MediaSource> Items { get; } = items.ToList();
    public Task AddMediaAsync(MediaSource source) { Items.Add(source); return Task.CompletedTask; }
    public Task<MediaSource?> GetMediaAsync(int id) => Task.FromResult(Items.FirstOrDefault(source => source.Id == id));
    public Task<List<MediaSource>> GetMediaListAsync() => Task.FromResult(Items.ToList());
    public Task DeleteMediaAsync(int id) { Items.RemoveAll(source => source.Id == id); return Task.CompletedTask; }
    public void UpdateMediaAsync(MediaSource source) { }
}
sealed class FakeUnitOfWork(IMediaSourceRepository repository) : IUnitOfWork
{
    public IMediaSourceRepository MediaSourceRepository => repository;
    public Task<int> CommitAsync() => Task.FromResult(1);
}
sealed class MemorySession : ISession
{
    private readonly Dictionary<string, byte[]> _values = new();
    public bool IsAvailable => true;
    public string Id => "regression-session";
    public IEnumerable<string> Keys => _values.Keys;
    public void Clear() => _values.Clear();
    public Task CommitAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task LoadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public void Remove(string key) => _values.Remove(key);
    public void Set(string key, byte[] value) => _values[key] = value;
    public bool TryGetValue(string key, out byte[] value) => _values.TryGetValue(key, out value!);
}
