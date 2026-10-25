using System.Collections.Concurrent;
using System.Threading.Channels;
using System.Text.Json;
using MediaParser.Models;
using MediaParser.Models.DBModels;
using MediaParser.Controllers;
using MediaParser.IRepositories;
using MediaParser.Repositories;
using MediaParser.Services;
using MediaParser.Services.DBServices;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

var tests = new (string Name, Func<Task> Run)[]
{
    ("Jobs belong to their session and duplicate active work is rejected", OwnershipAndDuplicates),
    ("Cancellation returns partial results promptly and ignores late checker output", CancellationPreservesPartialResults),
    ("Retry includes only unresolved outcomes and preserves source snapshots", RetryUnresolvedOnly),
    ("Worker concurrency and waiting queue are bounded", BoundedWorkersAndQueue),
    ("Cancelled workers keep their slot until the checker actually finishes", CancellationKeepsWorkerSlot),
    ("Retention preserves active work and expires completed results from their finish time", RetentionAndExpiry),
    ("Progress is monotonic and public snapshots do not contain API credentials", ProgressAndSafeOutput),
    ("Admission rejects empty or oversized requests and deduplicates identical work", AdmissionValidation),
    ("Checker exceptions become retryable sanitized results", CheckerExceptionIsSanitized),
    ("Controller validates mode and source membership and protects every job endpoint", ControllerValidationAndOwnership)
};
var failures = 0;
foreach (var test in tests)
{
    try
    {
        await test.Run();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception exception)
    {
        failures++;
        Console.Error.WriteLine($"FAIL {test.Name}: {exception.Message}");
    }
}
Console.WriteLine($"Passed {tests.Length - failures}/{tests.Length} background job regression tests.");
return failures == 0 ? 0 : 1;

static async Task OwnershipAndDuplicates()
{
    await using var fixture = await Fixture.Create();
    var started = Started(fixture.Service.TryStart("owner-a", Work(1)));
    var pending = await fixture.Checker.Next();
    Assert(fixture.Service.Get("owner-b", started.Id) == null, "Another session could inspect this job.");
    Assert(fixture.Service.Cancel("owner-b", started.Id) == null, "Another session could cancel this job.");
    var forbiddenRetry = fixture.Service.Retry("owner-b", started.Id);
    Assert(forbiddenRetry.Job == null && forbiddenRetry.StatusCode == 404, "Another session could retry this job or detect its existence.");
    var duplicate = fixture.Service.TryStart("owner-a", Work(2));
    Assert(duplicate.StatusCode == 409, "A second active search was accepted for the same session.");
    Assert(fixture.Checker.CallCount == 1, "Duplicate submission started additional work.");
    pending.Finish(AccountStatus.Found);
    await fixture.Finished("owner-a", started.Id);
    var next = Started(fixture.Service.TryStart("owner-a", Work(2)));
    (await fixture.Checker.Next()).Finish(AccountStatus.Found);
    await fixture.Finished("owner-a", next.Id);
}

static async Task CancellationPreservesPartialResults()
{
    await using var fixture = await Fixture.Create();
    var job = Started(fixture.Service.TryStart("owner", Work(1, 2, 3)));
    (await fixture.Checker.Next()).Finish(AccountStatus.Found);
    var blocked = await fixture.Checker.Next();
    var beforeCancel = fixture.Service.Get("owner", job.Id)!;
    Assert(beforeCancel.Completed == 1 && beforeCancel.Results.Single().Outcome == AccountStatus.Found,
        "The completed item was not published before the next check.");

    // This checker deliberately ignores cancellation and has no response yet.
    var cancelled = fixture.Service.Cancel("owner", job.Id)!;
    Assert(cancelled.State == "cancelled" && cancelled.CancellationRequested, "Cancellation did not immediately become terminal.");
    Assert(blocked.CancellationToken.IsCancellationRequested, "The active check did not receive cancellation.");
    Assert(cancelled.Completed == 1 && cancelled.Results.Count == 3, "Cancellation lost the partial result or did not represent remaining work.");
    Assert(cancelled.Results.Count(result => result.Outcome == AccountStatus.Found) == 1 &&
        cancelled.Results.Count(result => result.Outcome == AccountStatus.Cancelled) == 2,
        "Cancellation overwrote the successful result or left remaining work ambiguous.");
    Assert(fixture.Checker.CallCount == 2, "Cancellation ran the remaining sites.");
    blocked.Finish(AccountStatus.Found);
    await Eventually(() => fixture.Checker.Active == 0, "The cancelled worker did not finish after release.");
    var final = fixture.Service.Get("owner", job.Id)!;
    Assert(final.State == "cancelled" && final.Completed == 1 &&
        final.Results.Count(result => result.Outcome == AccountStatus.Found) == 1,
        "A late checker response overwrote the terminal cancelled snapshot.");
    Assert(fixture.Checker.CallCount == 2, "The worker continued to the third site after cancellation.");
    var retry = Started(fixture.Service.Retry("owner", job.Id));
    Assert(retry.Total == 2, "Retry of a cancelled job did not include exactly its unfinished items.");
    foreach (var expectedId in new[] { 2, 3 })
    {
        var invocation = await fixture.Checker.Next();
        Assert(invocation.Source.Id == expectedId, "Retry of a cancelled job repeated a finished site or omitted remaining work.");
        invocation.Finish(AccountStatus.Found);
    }
    await fixture.Finished("owner", retry.Id);
}

static async Task RetryUnresolvedOnly()
{
    await using var fixture = await Fixture.Create();
    var statuses = new[] { AccountStatus.Found, AccountStatus.NotFound, AccountStatus.Blocked,
        AccountStatus.Unconfirmed, AccountStatus.Error, AccountStatus.Cancelled };
    var items = Work(1, 2, 3, 4, 5, 6);
    var job = Started(fixture.Service.TryStart("owner", items));
    // The in-flight job must own its sources instead of observing later form/database edits.
    items[2].Source.Name = "changed after submit";
    items[2].Source.SearchUrl = "https://changed.example/{0}";
    for (var index = 0; index < statuses.Length; index++)
    {
        var invocation = await fixture.Checker.Next();
        if (invocation.Source.Id == 3)
            Assert(invocation.Source.Name == "Site 3" && invocation.Source.SearchUrl == "https://example.test/3/{0}",
                "The original source snapshot changed after submission.");
        invocation.Finish(statuses[index]);
    }
    var original = await fixture.Finished("owner", job.Id);
    Assert(original.CanRetry, "Unresolved outcomes did not enable retry.");
    var retried = Started(fixture.Service.Retry("owner", job.Id));
    Assert(retried.Id != job.Id && retried.Total == 4, "Retry did not create a job with only unresolved items.");
    var retriedIds = new List<int>();
    for (var index = 0; index < 4; index++)
    {
        var invocation = await fixture.Checker.Next();
        retriedIds.Add(invocation.Source.Id);
        invocation.Finish(AccountStatus.Found);
    }
    var finished = await fixture.Finished("owner", retried.Id);
    Assert(retriedIds.Order().SequenceEqual(new[] { 3, 4, 5, 6 }), "Retry repeated Found/NotFound or omitted an unresolved status.");
    Assert(finished.Completed == 4 && !finished.CanRetry, "A fully resolved retry still advertises retry.");
    Assert(fixture.Service.Get("owner", job.Id)!.Results.Count == 6, "Retry destroyed the original results.");
    var unnecessary = fixture.Service.Retry("owner", retried.Id);
    Assert(unnecessary.Job == null && unnecessary.StatusCode is >= 400 and < 500, "A resolved job was needlessly retried.");
}

static async Task BoundedWorkersAndQueue()
{
    await using var fixture = await Fixture.Create(workerCount: 2, queueCapacity: 1);
    var first = Started(fixture.Service.TryStart("first", Work(1)));
    var callOne = await fixture.Checker.Next();
    var second = Started(fixture.Service.TryStart("second", Work(2)));
    var callTwo = await fixture.Checker.Next();
    var third = Started(fixture.Service.TryStart("third", Work(3)));
    var overflow = fixture.Service.TryStart("overflow", Work(4));
    Assert(overflow.Job == null && overflow.StatusCode == 429, "The waiting queue admitted work beyond its capacity.");
    Assert(fixture.Checker.CallCount == 2 && fixture.Checker.MaxActive == 2, "The configured worker limit was exceeded.");
    callOne.Finish(AccountStatus.Found);
    var callThree = await fixture.Checker.Next();
    Assert(callThree.Source.Id == 3 && fixture.Checker.MaxActive == 2, "Queued work bypassed or exceeded the worker bound.");
    callTwo.Finish(AccountStatus.Found);
    callThree.Finish(AccountStatus.Found);
    await fixture.Finished("first", first.Id);
    await fixture.Finished("second", second.Id);
    await fixture.Finished("third", third.Id);
    var acceptedAfterOverflow = Started(fixture.Service.TryStart("overflow", Work(4)));
    (await fixture.Checker.Next()).Finish(AccountStatus.Found);
    await fixture.Finished("overflow", acceptedAfterOverflow.Id);
}

static async Task CancellationKeepsWorkerSlot()
{
    await using var fixture = await Fixture.Create(workerCount: 1, queueCapacity: 2);
    var first = Started(fixture.Service.TryStart("first", Work(1)));
    var blocked = await fixture.Checker.Next();
    var next = Started(fixture.Service.TryStart("second", Work(2)));
    Assert(fixture.Service.Cancel("first", first.Id)?.State == "cancelled", "Cancellation failed.");
    Assert(fixture.Checker.Active == 1 && fixture.Checker.CallCount == 1,
        "Cancellation freed a worker slot while its browser/checker was still active.");
    Assert(fixture.Service.Get("second", next.Id)!.Completed == 0, "The queued job ran while the cancelled checker still occupied the only worker.");
    blocked.Finish(AccountStatus.Found);
    var subsequent = await fixture.Checker.Next();
    Assert(subsequent.Source.Id == 2 && fixture.Checker.MaxActive == 1, "A cancelled checker overlapped its replacement.");
    subsequent.Finish(AccountStatus.Found);
    await fixture.Finished("second", next.Id);
}

static async Task RetentionAndExpiry()
{
    var retention = TimeSpan.FromMinutes(10);
    await using var fixture = await Fixture.Create(maxRetainedJobs: 1, retention: retention);
    var original = Started(fixture.Service.TryStart("owner", Work(1)));
    var pending = await fixture.Checker.Next();
    var noCapacity = fixture.Service.TryStart("another", Work(2));
    Assert(noCapacity.Job == null && noCapacity.StatusCode == 429, "The retained job limit was exceeded.");
    fixture.Time.Advance(retention + TimeSpan.FromMinutes(1));
    Assert(fixture.Service.PruneExpired() == 0 && fixture.Service.Get("owner", original.Id) != null,
        "An active job expired before it finished.");
    pending.Finish(AccountStatus.Found);
    await fixture.Finished("owner", original.Id);
    Assert(fixture.Service.PruneExpired() == 0, "Retention was measured from job start instead of completion.");
    fixture.Time.Advance(retention - TimeSpan.FromSeconds(1));
    Assert(fixture.Service.PruneExpired() == 0, "A completed result expired before the configured retention.");
    fixture.Time.Advance(TimeSpan.FromSeconds(2));
    var pruned = 0;
    await Eventually(() => (pruned += fixture.Service.PruneExpired()) == 1,
        "Expired results remained retained after their worker released its slot.");
    Assert(fixture.Service.Get("owner", original.Id) == null, "An expired result remained visible.");
    var admitted = Started(fixture.Service.TryStart("another", Work(2)));
    (await fixture.Checker.Next()).Finish(AccountStatus.Found);
    await fixture.Finished("another", admitted.Id);
}

static async Task ProgressAndSafeOutput()
{
    await using var fixture = await Fixture.Create();
    var items = Work(1, 2, 3, 4);
    const string dummySecret = "TEST-ONLY-job-snapshot-secret-384271";
    foreach (var item in items) item.Source.ApiKey = dummySecret;
    var job = Started(fixture.Service.TryStart("owner", items));
    var lastCompleted = 0;
    for (var index = 0; index < 4; index++)
    {
        var invocation = await fixture.Checker.Next();
        var snapshot = fixture.Service.Get("owner", job.Id)!;
        Assert(snapshot.Total == 4 && snapshot.Completed >= lastCompleted && snapshot.Completed == index,
            "Progress decreased, changed its total, or skipped a completed check.");
        Assert(snapshot.Results.Count == index, "Partial result rows did not match the progress counter.");
        var json = JsonSerializer.Serialize(snapshot);
        Assert(!json.Contains(dummySecret, StringComparison.Ordinal) && !json.Contains("ApiKey", StringComparison.OrdinalIgnoreCase),
            "The progress response exposed source credentials.");
        lastCompleted = snapshot.Completed;
        invocation.Finish(index == 1 ? AccountStatus.NotFound : AccountStatus.Found);
    }
    var final = await fixture.Finished("owner", job.Id);
    Assert(final.Completed == 4 && final.Results.Count == 4 && final.Total == 4 && !final.CanRetry,
        "Final progress does not account for every resolved item.");
    Assert(!JsonSerializer.Serialize(final).Contains(dummySecret, StringComparison.Ordinal), "The final response exposed source credentials.");
}

static async Task AdmissionValidation()
{
    await using var fixture = await Fixture.Create();
    Assert(fixture.Service.TryStart("owner", Array.Empty<SearchWorkItem>()).StatusCode == 400,
        "An empty request was admitted.");
    Assert(fixture.Service.TryStart("owner", Work(Enumerable.Range(1, 251).ToArray())).StatusCode == 400,
        "A request beyond the work-item bound was admitted.");
    Assert(fixture.Service.TryStart("", Work(1)).StatusCode == 400, "An ownerless request was admitted.");
    var job = Started(fixture.Service.TryStart("owner", Work(1, 1)));
    Assert(job.Total == 1, "Identical source and identifier work was not deduplicated.");
    (await fixture.Checker.Next()).Finish(AccountStatus.Found);
    await fixture.Finished("owner", job.Id);
    Assert(fixture.Checker.CallCount == 1, "Duplicate work reached the checker.");
}

static async Task CheckerExceptionIsSanitized()
{
    await using var fixture = await Fixture.Create();
    var job = Started(fixture.Service.TryStart("owner", Work(1, 2)));
    const string exceptionSecret = "TEST-ONLY-exception-secret-927134";
    (await fixture.Checker.Next()).Response.TrySetException(new InvalidOperationException(exceptionSecret));
    (await fixture.Checker.Next()).Finish(AccountStatus.Found);
    var final = await fixture.Finished("owner", job.Id);
    Assert(final.State == "completed" && final.Completed == 2 && final.Results.Count == 2,
        "A single checker exception aborted or lost unrelated source checks.");
    Assert(final.Results.Single(result => result.SiteId == 1).Outcome == AccountStatus.Error && final.CanRetry,
        "A checker exception did not become a retryable source error.");
    Assert(!JsonSerializer.Serialize(final).Contains(exceptionSecret, StringComparison.Ordinal),
        "An internal checker exception leaked into the public progress response.");
}

static async Task ControllerValidationAndOwnership()
{
    await using var fixture = await Fixture.Create();
    var user = Work(1)[0].Source;
    var fullName = Work(2)[0].Source;
    fullName.LinkType = "FullName";
    fullName.SearchUrl = "https://example.test/search?keywords={0}";
    var (controller, session) = CreateController(fixture.Service, user, fullName);
    var invalid = new[]
    {
        new SearchRequest { Mode = "Unknown", Identifiers = new() { "alice" }, SourceIds = new() { 1 } },
        new SearchRequest { Mode = "User", Identifiers = new(), SourceIds = new() { 1 } },
        new SearchRequest { Mode = "User", Identifiers = new() { "Alice Bob" }, SourceIds = new() { 1 } },
        new SearchRequest { Mode = "User", Identifiers = new() { new string('a', 101) }, SourceIds = new() { 1 } },
        new SearchRequest { Mode = "User", Identifiers = Enumerable.Range(1, 6).Select(index => $"user{index}").ToList(), SourceIds = new() { 1 } },
        new SearchRequest { Mode = "User", Identifiers = new() { "alice" }, SourceIds = new() },
        new SearchRequest { Mode = "User", Identifiers = new() { "alice" }, SourceIds = new() { 404 } },
        new SearchRequest { Mode = "User", Identifiers = new() { "alice" }, SourceIds = new() { 1, 2 } },
        new SearchRequest { Mode = "FullName", Identifiers = new() { "Alice Bob" }, SourceIds = new() { 1 } }
    };
    foreach (var request in invalid)
    {
        var result = await controller.StartSearch(request);
        Assert(result is ObjectResult { StatusCode: 400 }, "The controller admitted invalid identifiers, mode, or source membership.");
    }
    Assert(await controller.StartSearch(null) is ObjectResult { StatusCode: 400 }, "A null JSON search request was admitted.");
    Assert(fixture.Checker.CallCount == 0, "An invalid request reached the checker.");

    var started = await controller.StartSearch(new SearchRequest
    {
        Mode = "FullName", Identifiers = new() { " Alice Bob ", "alice bob" }, SourceIds = new() { 2, 2 }
    });
    Assert(started is ObjectResult { StatusCode: 202, Value: SearchJobSnapshot }, "A valid full-name request was rejected.");
    var job = (SearchJobSnapshot)((ObjectResult)started).Value!;
    Assert(job.Total == 1, "Duplicate source IDs or case-insensitive identifiers were checked more than once.");
    var invocation = await fixture.Checker.Next();
    Assert(invocation.Source.Id == 2 && invocation.Source.LinkType == "FullName" && invocation.Identifier == "Alice Bob",
        "The mode filter checked the wrong source or did not trim the identifier.");
    Assert(!string.IsNullOrWhiteSpace(session.GetString("SearchOwner")), "The server did not persist a session owner before returning the job.");
    invocation.Finish(AccountStatus.Unconfirmed);
    await fixture.Finished(session.GetString("SearchOwner")!, job.Id);
    Assert(controller.SearchProgress(job.Id) is JsonResult { Value: SearchJobSnapshot }, "The owner could not read completed progress.");
    Assert(controller.ExportJob(job.Id) is FileContentResult { FileContents.Length: > 0 }, "The owner could not export completed results.");

    var (stranger, _) = CreateController(fixture.Service, user, fullName);
    Assert(stranger.SearchProgress(job.Id) is NotFoundObjectResult, "Progress exposed another session's job.");
    Assert(stranger.CancelSearch(job.Id) is NotFoundObjectResult, "Cancel exposed another session's job.");
    Assert(stranger.RetrySearch(job.Id) is NotFoundObjectResult, "Retry exposed another session's job.");
    Assert(stranger.ExportJob(job.Id) is NotFoundObjectResult, "Export exposed another session's results.");
}

static (ParserController Controller, MemorySession Session) CreateController(SearchJobService jobs, params MediaSource[] sources)
{
    var session = new MemorySession();
    var context = new DefaultHttpContext { Session = session };
    var accessor = new HttpContextAccessor { HttpContext = context };
    var database = new MediaSourceService(new FakeUnitOfWork(new FakeRepository(sources)), NullLogger<MediaSourceService>.Instance);
    var media = new MediaService(database, accessor);
    var controller = new ParserController(media, new ResultsRepository(accessor), jobs)
    {
        ControllerContext = new ControllerContext { HttpContext = context }
    };
    return (controller, session);
}

static List<SearchWorkItem> Work(params int[] ids) => ids.Select(id => new SearchWorkItem(new MediaSource
{
    Id = id, Name = $"Site {id}", SearchUrl = $"https://example.test/{id}/{{0}}", ErrorKeywordsRaw = "[]", LinkType = "User"
}, "test-identifier")).ToList();

static SearchJobSnapshot Started(SearchJobStartResult result)
{
    Assert(result.Job != null && result.StatusCode is >= 200 and < 300, $"Expected an admitted job, got {result.StatusCode}: {result.Error}");
    return result.Job!;
}

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static async Task Eventually(Func<bool> condition, string failure)
{
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    try
    {
        while (!condition()) await Task.Delay(10, deadline.Token);
    }
    catch (OperationCanceledException) when (deadline.IsCancellationRequested)
    {
        throw new InvalidOperationException(failure);
    }
}

sealed class Fixture : IAsyncDisposable
{
    public ControlledChecker Checker { get; } = new();
    public ManualTimeProvider Time { get; } = new();
    public SearchJobService Service { get; }

    private Fixture(int workerCount, int queueCapacity, int maxRetainedJobs, TimeSpan retention)
    {
        Service = new SearchJobService(Checker, Options.Create(new SearchJobOptions
        {
            WorkerCount = workerCount, QueueCapacity = queueCapacity,
            MaxRetainedJobs = maxRetainedJobs, Retention = retention
        }), Time, NullLogger<SearchJobService>.Instance);
    }

    public static async Task<Fixture> Create(int workerCount = 1, int queueCapacity = 8,
        int maxRetainedJobs = 100, TimeSpan? retention = null)
    {
        var fixture = new Fixture(workerCount, queueCapacity, maxRetainedJobs, retention ?? TimeSpan.FromHours(2));
        await fixture.Service.StartAsync(CancellationToken.None);
        return fixture;
    }

    public async Task<SearchJobSnapshot> Finished(string owner, Guid id)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true)
        {
            var snapshot = Service.Get(owner, id) ?? throw new InvalidOperationException("The job disappeared before completion.");
            if (snapshot.State is "completed" or "cancelled" or "failed") return snapshot;
            try { await Task.Delay(10, deadline.Token); }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested)
            {
                throw new InvalidOperationException($"The job did not become terminal; state={snapshot.State}, completed={snapshot.Completed}/{snapshot.Total}.");
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        Checker.ReleaseAll();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await Service.StopAsync(deadline.Token);
        Service.Dispose();
    }
}

sealed class ControlledChecker : IAccountChecker
{
    private readonly Channel<Invocation> _calls = Channel.CreateUnbounded<Invocation>();
    private readonly ConcurrentBag<Invocation> _all = new();
    private int _active;
    private int _maxActive;
    private int _callCount;
    public int Active => Volatile.Read(ref _active);
    public int MaxActive => Volatile.Read(ref _maxActive);
    public int CallCount => Volatile.Read(ref _callCount);

    public async Task<AccountCheckResult> CheckAsync(MediaSource source, string identifier, CancellationToken cancellationToken)
    {
        var active = Interlocked.Increment(ref _active);
        int observed;
        do
        {
            observed = Volatile.Read(ref _maxActive);
            if (observed >= active) break;
        } while (Interlocked.CompareExchange(ref _maxActive, active, observed) != observed);
        Interlocked.Increment(ref _callCount);
        var invocation = new Invocation(source, identifier, cancellationToken);
        _all.Add(invocation);
        _calls.Writer.TryWrite(invocation);
        try { return await invocation.Response.Task; }
        finally { Interlocked.Decrement(ref _active); }
    }

    public async Task<Invocation> Next()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { return await _calls.Reader.ReadAsync(deadline.Token); }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            throw new InvalidOperationException("The expected checker invocation did not start.");
        }
    }

    public void ReleaseAll()
    {
        foreach (var invocation in _all) invocation.Finish(AccountStatus.Cancelled);
    }
}

sealed class Invocation(MediaSource source, string identifier, CancellationToken cancellationToken)
{
    public MediaSource Source { get; } = source;
    public string Identifier { get; } = identifier;
    public CancellationToken CancellationToken { get; } = cancellationToken;
    public TaskCompletionSource<AccountCheckResult> Response { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public void Finish(AccountStatus status) => Response.TrySetResult(new AccountCheckResult(status, "Deterministic test result."));
}

sealed class ManualTimeProvider : TimeProvider
{
    private long _ticks = new DateTimeOffset(2026, 10, 25, 9, 0, 0, TimeSpan.Zero).Ticks;
    public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);
    public void Advance(TimeSpan amount) => Interlocked.Add(ref _ticks, amount.Ticks);
}

sealed class FakeRepository(IEnumerable<MediaSource> items) : IMediaSourceRepository
{
    private readonly List<MediaSource> _items = items.ToList();
    public Task AddMediaAsync(MediaSource source) { _items.Add(source); return Task.CompletedTask; }
    public Task<MediaSource?> GetMediaAsync(int id) => Task.FromResult(_items.FirstOrDefault(source => source.Id == id));
    public Task<List<MediaSource>> GetMediaListAsync() => Task.FromResult(_items.ToList());
    public Task DeleteMediaAsync(int id) { _items.RemoveAll(source => source.Id == id); return Task.CompletedTask; }
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
    public string Id { get; } = Guid.NewGuid().ToString("N");
    public IEnumerable<string> Keys => _values.Keys;
    public void Clear() => _values.Clear();
    public Task CommitAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task LoadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public void Remove(string key) => _values.Remove(key);
    public void Set(string key, byte[] value) => _values[key] = value;
    public bool TryGetValue(string key, out byte[] value) => _values.TryGetValue(key, out value!);
}
