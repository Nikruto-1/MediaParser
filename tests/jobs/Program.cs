using System.Collections.Concurrent;
using System.IO.Compression;
using System.Threading.Channels;
using System.Text.Json;
using System.Xml.Linq;
using MediaParser.Classes;
using MediaParser.Models;
using MediaParser.Models.DBModels;
using MediaParser.Controllers;
using MediaParser.IRepositories;
using MediaParser.Repositories;
using MediaParser.Services;
using MediaParser.Services.DBServices;
using MediaParser.Services.Captcha;
using MediaParser.Services.Declarations;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

var tests = new (string Name, Func<Task> Run)[]
{
    ("Automatic CAPTCHA is opt-in, has a shared per-search budget and never runs in manual mode", AutomaticCaptchaBudget),
    ("Jobs belong to their session and duplicate active work is rejected", OwnershipAndDuplicates),
    ("Cancellation returns partial results promptly and ignores late checker output", CancellationPreservesPartialResults),
    ("Retry includes only unresolved outcomes and preserves source snapshots", RetryUnresolvedOnly),
    ("Worker concurrency and waiting queue are bounded", BoundedWorkersAndQueue),
    ("Cancelled workers keep their slot until the checker actually finishes", CancellationKeepsWorkerSlot),
    ("Retention preserves active work and expires completed results from their finish time", RetentionAndExpiry),
    ("Progress is monotonic and public snapshots do not contain API credentials", ProgressAndSafeOutput),
    ("Admission rejects empty or oversized requests and deduplicates identical work", AdmissionValidation),
    ("Checker exceptions become retryable sanitized results", CheckerExceptionIsSanitized),
    ("Controller validates mode and source membership and protects every job endpoint", ControllerValidationAndOwnership),
    ("Every planned source appears immediately without counting pending or checking rows", PlannedRowsAndProgress),
    ("Manual checks validate ownership and target status and preserve every other result", ManualValidationAndPreservation),
    ("Cancelling a manual check preserves retained rows and holds the occupied worker slot", ManualCancellationAndSlots),
    ("Manual checking targets one source and identifier pair without changing its other identifiers", ManualTargetPairIsolation),
    ("Ordinary retry of an unresolved manual result uses only the non-interactive checker", OrdinaryRetryIsNotInteractive),
    ("Declaration metadata survives progress and history and is retained when other sources are retried or checked manually", DeclarationMetadataRoundtrip),
    ("Excel exports rich declarations on a linked worksheet and preserves the five legacy result columns", DeclarationExcelExport),
    ("YouControl handoff keeps its public URL in history and export and is not retried as an automated lookup", YouControlSearchRoundtrip)
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

static async Task AutomaticCaptchaBudget()
{
    await using (var disabled = await Fixture.Create())
    {
        Assert(disabled.Service.TryStart("owner", Work(1), automaticCaptcha: true).StatusCode == 400,
            "An unconfigured paid integration was accepted.");
        Assert(disabled.Checker.CallCount == 0, "Disabled CAPTCHA started a checker.");
    }
    var options = new CaptchaOptions { Enabled = true, ApiKey = "fixture-private-captcha-key", MaxTasksPerSearch = 1 };
    await using var fixture = await Fixture.Create(captchaOptions: options);
    var ordinary = Started(fixture.Service.TryStart("owner", Work(1)));
    var regular = await fixture.Checker.Next();
    Assert(regular.CaptchaBudget == null && !ordinary.AutomaticCaptcha, "A normal search opted into paid CAPTCHA.");
    regular.Finish(AccountStatus.Found);
    await fixture.Finished("owner", ordinary.Id);

    var automatic = Started(fixture.Service.TryStart("owner", Work(1, 2), automaticCaptcha: true));
    Assert(automatic.AutomaticCaptcha, "The opted-in search lost its CAPTCHA setting.");
    var first = await fixture.Checker.Next();
    Assert(first.CaptchaBudget != null && first.CaptchaBudget.TryReserve(), "The automatic checker did not receive its budget.");
    first.ReportProgress?.Invoke("Розв’язуємо капчу через 2Captcha…");
    Assert(fixture.Service.Get("owner", automatic.Id)!.ProgressMessage == "Розв’язуємо капчу через 2Captcha…",
        "CAPTCHA progress was not published.");
    first.Finish(AccountStatus.Blocked);
    var second = await fixture.Checker.Next();
    Assert(ReferenceEquals(first.CaptchaBudget, second.CaptchaBudget) && !second.CaptchaBudget!.TryReserve(),
        "Each source received a new spending budget instead of sharing the search limit.");
    second.Finish(AccountStatus.Found);
    var complete = await fixture.Finished("owner", automatic.Id);
    Assert(complete.ProgressMessage == null && !JsonSerializer.Serialize(complete).Contains(options.ApiKey, StringComparison.Ordinal),
        "A terminal snapshot kept transient progress or leaked the CAPTCHA key.");

    var retry = Started(fixture.Service.Retry("owner", automatic.Id));
    var retried = await fixture.Checker.Next();
    Assert(retry.AutomaticCaptcha && retry.Total == 1 && retried.CaptchaBudget?.Used == 0 &&
        !ReferenceEquals(first.CaptchaBudget, retried.CaptchaBudget), "Retry did not create a new bounded CAPTCHA budget.");
    retried.Finish(AccountStatus.Unconfirmed);
    await fixture.Finished("owner", retry.Id);
    var manual = Started(fixture.Service.TryStartManual("owner", automatic.Id, 1, "test-identifier"));
    var interactive = await fixture.Checker.Next();
    Assert(manual.IsInteractive && !manual.AutomaticCaptcha && interactive.IsInteractive && interactive.CaptchaBudget == null,
        "Manual Chrome unexpectedly invoked a paid CAPTCHA solver.");
    interactive.Finish(AccountStatus.Unconfirmed);
    await fixture.Finished("owner", manual.Id);
}

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
    Assert(controller.ManualCheck(job.Id, null) is ObjectResult { StatusCode: 400 }, "Manual checking accepted a null request body.");
    Assert(controller.ManualCheck(job.Id, new ManualCheckRequest { SiteId = 404, Identifier = "Alice Bob" }) is ObjectResult { StatusCode: 400 },
        "Manual checking accepted a source outside the owned job.");
    Assert(controller.ManualCheck(Guid.NewGuid(), new ManualCheckRequest { SiteId = 2, Identifier = "Alice Bob" }) is NotFoundObjectResult,
        "Manual checking did not reject an unknown job.");

    var (stranger, _) = CreateController(fixture.Service, user, fullName);
    Assert(stranger.SearchProgress(job.Id) is NotFoundObjectResult, "Progress exposed another session's job.");
    Assert(stranger.CancelSearch(job.Id) is NotFoundObjectResult, "Cancel exposed another session's job.");
    Assert(stranger.RetrySearch(job.Id) is NotFoundObjectResult, "Retry exposed another session's job.");
    Assert(stranger.ExportJob(job.Id) is NotFoundObjectResult, "Export exposed another session's results.");
    Assert(stranger.ManualCheck(job.Id, new ManualCheckRequest { SiteId = 2, Identifier = "Alice Bob" }) is NotFoundObjectResult,
        "Manual checking exposed another session's job.");
}

static async Task PlannedRowsAndProgress()
{
    await using var fixture = await Fixture.Create();
    var items = Work(1, 2, 3);
    const string secret = "TEST-ONLY-planned-row-secret-742619";
    foreach (var item in items)
    {
        item.Source.ApiUrl = $"https://private.example/api/{item.Source.Id}/{{0}}";
        item.Source.ApiKey = secret;
    }
    var initial = Started(fixture.Service.TryStart("owner", items));
    var planned = Planned(initial);
    Assert(!initial.IsInteractive && initial.Completed == 0 && initial.Results.Count == 0,
        "Initial progress counted unfinished work or marked an ordinary job interactive.");
    Assert(planned.Count == 3 && planned.All(row => row.Outcome == AccountStatus.Pending && row.CheckedAt == null),
        "All selected sources were not present as timestamp-free pending rows in the admission response.");
    Assert(planned.Select(row => row.SiteId).Order().SequenceEqual(new[] { 1, 2, 3 }),
        "The planned rows did not match the selected sources.");
    var first = await fixture.Checker.Next();
    var running = fixture.Service.Get("owner", initial.Id)!;
    planned = Planned(running);
    Assert(planned.Count(row => row.Outcome == AccountStatus.Checking) == 1 &&
        planned.Single(row => row.Outcome == AccountStatus.Checking).SiteId == 1 &&
        planned.Count(row => row.Outcome == AccountStatus.Pending) == 2,
        "The active item was not distinguished from queued sources.");
    Assert(planned.All(row => row.CheckedAt == null) && running.Completed == 0 && running.Results.Count == 0,
        "Pending or checking placeholders were counted as completed checks or given check timestamps.");
    var publicJson = JsonSerializer.Serialize(running);
    Assert(!publicJson.Contains(secret, StringComparison.Ordinal) && !publicJson.Contains("ApiKey", StringComparison.OrdinalIgnoreCase)
        && !publicJson.Contains("private.example", StringComparison.Ordinal), "Planned rows exposed private source credentials or API addresses.");

    first.Finish(AccountStatus.Found);
    var second = await fixture.Checker.Next();
    var partial = fixture.Service.Get("owner", initial.Id)!;
    planned = Planned(partial);
    Assert(partial.Completed == 1 && partial.Results.Count == 1 && planned.Count == 3,
        "Partial progress included placeholders or omitted planned sources.");
    Assert(planned.Single(row => row.SiteId == 1).Outcome == AccountStatus.Found &&
        planned.Single(row => row.SiteId == 1).CheckedAt != null &&
        planned.Single(row => row.SiteId == 2).Outcome == AccountStatus.Checking &&
        planned.Single(row => row.SiteId == 3).Outcome == AccountStatus.Pending,
        "The plan did not merge retained, active, and pending rows correctly.");
    second.Finish(AccountStatus.NotFound);
    (await fixture.Checker.Next()).Finish(AccountStatus.Unconfirmed);
    var final = await fixture.Finished("owner", initial.Id);
    Assert(final.Completed == 3 && Planned(final).All(row => row.Outcome is not (AccountStatus.Pending or AccountStatus.Checking)),
        "Completed jobs still contain pending or checking placeholders.");
}

static async Task ManualValidationAndPreservation()
{
    await using var fixture = await Fixture.Create();
    var items = Work(1, 2, 3, 4);
    items[2].Source.ApiUrl = "https://private.example/api/{0}";
    items[2].Source.ApiKey = "TEST-ONLY-manual-source-secret-752491";
    var original = Started(fixture.Service.TryStart("owner", items));
    var first = await fixture.Checker.Next();
    Assert(fixture.Service.TryStartManual("stranger", original.Id, 3, "test-identifier").StatusCode == 404,
        "A foreign session could request a manual check or discover the original job.");
    Assert(fixture.Service.TryStartManual("owner", Guid.NewGuid(), 3, "test-identifier").StatusCode == 404,
        "An unknown job did not return 404 for manual checking.");
    Assert(fixture.Service.TryStartManual("owner", original.Id, 3, "test-identifier").StatusCode == 409,
        "Manual checking was allowed before the original job finished.");
    first.Finish(AccountStatus.Found);
    (await fixture.Checker.Next()).Finish(AccountStatus.NotFound);
    (await fixture.Checker.Next()).Finish(AccountStatus.Blocked);
    (await fixture.Checker.Next()).Finish(AccountStatus.Error);
    var before = await fixture.Finished("owner", original.Id);
    Assert(fixture.Service.TryStartManual("owner", original.Id, 1, "test-identifier").StatusCode == 400,
        "A confirmed found row was admitted for manual checking.");
    Assert(fixture.Service.TryStartManual("owner", original.Id, 2, "test-identifier").StatusCode == 400,
        "A confirmed absent row was admitted for manual checking.");
    Assert(fixture.Service.TryStartManual("owner", original.Id, 404, "test-identifier").StatusCode == 400,
        "An unknown source was admitted for manual checking.");
    Assert(fixture.Service.TryStartManual("owner", original.Id, 3, "different-identifier").StatusCode == 400,
        "A mismatched identifier was admitted for manual checking.");

    var another = Started(fixture.Service.TryStart("owner", Work(5)));
    var occupied = await fixture.Checker.Next();
    Assert(fixture.Service.TryStartManual("owner", original.Id, 3, "test-identifier").StatusCode == 409,
        "Manual checking bypassed another active job belonging to the same session.");
    occupied.Finish(AccountStatus.Found);
    await fixture.Finished("owner", another.Id);

    fixture.Time.Advance(TimeSpan.FromMinutes(1));
    var manual = Started(fixture.Service.TryStartManual("owner", original.Id, 3, "test-identifier"));
    Assert(manual.Id != original.Id && manual.IsInteractive && manual.Total == 4 && manual.Completed == 3 && manual.Results.Count == 3,
        "Manual checking did not create a new job retaining all non-target results and their progress.");
    foreach (var retained in before.Results.Where(row => row.SiteId != 3))
        Assert(manual.Results.Single(row => row.SiteId == retained.SiteId) == retained,
            "Manual admission changed a retained result or its original timestamp.");
    Assert(Planned(manual).Count == 4 && Planned(manual).Single(row => row.SiteId == 3).Outcome == AccountStatus.Pending,
        "The manual target was not exposed as pending alongside retained rows.");
    var interactive = await fixture.Checker.Next();
    Assert(interactive.IsInteractive && interactive.Source.Id == 3 && interactive.Identifier == "test-identifier",
        "Manual work did not use the interactive checker for exactly the requested target.");
    Assert(interactive.Source.ApiUrl == null && interactive.Source.ApiKey == null &&
        interactive.Source.SearchUrl == "https://example.test/3/{0}",
        "Manual work retained API credentials or lost its public web URL.");
    var checking = fixture.Service.Get("owner", manual.Id)!;
    Assert(checking.Completed == 3 && checking.Results.Count == 3 &&
        Planned(checking).Single(row => row.SiteId == 3).Outcome == AccountStatus.Checking &&
        Planned(checking).Single(row => row.SiteId == 3).CheckedAt == null,
        "The interactive target was counted as completed before its check finished.");
    interactive.Finish(AccountStatus.Found);
    var final = await fixture.Finished("owner", manual.Id);
    Assert(final.Completed == 4 && final.Results.Count == 4 && final.IsInteractive,
        "The completed manual job lost retained rows or incorrect progress.");
    foreach (var retained in before.Results.Where(row => row.SiteId != 3))
        Assert(final.Results.Single(row => row.SiteId == retained.SiteId) == retained,
            "Manual completion rewrote an unrelated result or its timestamp.");
    var changed = final.Results.Single(row => row.SiteId == 3);
    Assert(changed.Outcome == AccountStatus.Found && changed.CheckedAt > before.Results.Single(row => row.SiteId == 3).CheckedAt,
        "The manual target did not receive its new outcome and check timestamp.");
    Assert(fixture.Service.Get("owner", original.Id)!.Results.Single(row => row.SiteId == 3).Outcome == AccountStatus.Blocked,
        "Manual checking overwrote the original job's history.");
    Assert(fixture.Checker.CallCount == 6 && fixture.Checker.InteractiveCallCount == 1,
        "Manual checking repeated retained sources or used the normal checker.");
    var json = JsonSerializer.Serialize(final);
    Assert(!json.Contains("TEST-ONLY-manual-source-secret-752491", StringComparison.Ordinal) &&
        !json.Contains("private.example", StringComparison.Ordinal), "Manual progress exposed credentials or private API addresses.");
}

static async Task ManualCancellationAndSlots()
{
    await using var fixture = await Fixture.Create(workerCount: 1, queueCapacity: 2);
    var original = Started(fixture.Service.TryStart("owner", Work(1, 2, 3)));
    (await fixture.Checker.Next()).Finish(AccountStatus.Found);
    (await fixture.Checker.Next()).Finish(AccountStatus.Blocked);
    (await fixture.Checker.Next()).Finish(AccountStatus.Error);
    var retained = await fixture.Finished("owner", original.Id);
    var manual = Started(fixture.Service.TryStartManual("owner", original.Id, 2, "test-identifier"));
    var interactive = await fixture.Checker.Next();
    Assert(interactive.IsInteractive, "The manual checker did not start.");
    var successor = Started(fixture.Service.TryStart("next-owner", Work(4)));
    var cancelled = fixture.Service.Cancel("owner", manual.Id)!;
    Assert(cancelled.State == "cancelled" && cancelled.Completed == 2 && cancelled.Results.Count == 3,
        "Manual cancellation lost retained progress or counted the target as checked.");
    foreach (var row in retained.Results.Where(row => row.SiteId != 2))
        Assert(cancelled.Results.Single(result => result.SiteId == row.SiteId) == row,
            "Manual cancellation changed a retained row or its timestamp.");
    Assert(cancelled.Results.Single(row => row.SiteId == 2).Outcome == AccountStatus.Cancelled &&
        interactive.CancellationToken.IsCancellationRequested, "The cancelled target or token state was incorrect.");
    Assert(fixture.Checker.CallCount == 4 && fixture.Checker.Active == 1 && fixture.Checker.MaxActive == 1,
        "Manual cancellation released its worker slot before the checker stopped.");
    interactive.Finish(AccountStatus.Found);
    var next = await fixture.Checker.Next();
    Assert(!next.IsInteractive && next.Source.Id == 4 && fixture.Checker.MaxActive == 1,
        "Queued work overlapped the cancelled interactive check.");
    next.Finish(AccountStatus.Found);
    await fixture.Finished("next-owner", successor.Id);
    var final = fixture.Service.Get("owner", manual.Id)!;
    Assert(final.Completed == 2 && final.Results.Single(row => row.SiteId == 2).Outcome == AccountStatus.Cancelled,
        "A late interactive response overwrote the cancelled manual result.");
}

static async Task ManualTargetPairIsolation()
{
    await using var fixture = await Fixture.Create();
    var source = Work(1)[0].Source;
    var items = new[] { new SearchWorkItem(source, "first"), new SearchWorkItem(source, "second") };
    var original = Started(fixture.Service.TryStart("owner", items));
    Assert(Planned(original).Count == 2 && Planned(original).All(row => row.Outcome == AccountStatus.Pending),
        "Multiple identifiers at the same source were collapsed in planned rows.");
    (await fixture.Checker.Next()).Finish(AccountStatus.Blocked);
    (await fixture.Checker.Next()).Finish(AccountStatus.NotFound);
    var finished = await fixture.Finished("owner", original.Id);
    var retained = finished.Results.Single(row => row.Identifier == "second");
    var manual = Started(fixture.Service.TryStartManual("owner", original.Id, 1, "first"));
    Assert(manual.Total == 2 && manual.Completed == 1 && manual.Results.Single() == retained,
        "Manual admission removed a different identifier at the same source.");
    var invocation = await fixture.Checker.Next();
    Assert(invocation.IsInteractive && invocation.Source.Id == 1 && invocation.Identifier == "first",
        "Manual work selected the wrong source and identifier pair.");
    invocation.Finish(AccountStatus.Found);
    var final = await fixture.Finished("owner", manual.Id);
    Assert(final.Results.Count == 2 && final.Results.Single(row => row.Identifier == "second") == retained &&
        final.Results.Single(row => row.Identifier == "first").Outcome == AccountStatus.Found,
        "A single-row manual result overwrote another identifier at the same source.");
    Assert(fixture.Checker.InteractiveCallCount == 1 && fixture.Checker.CallCount == 3,
        "Manual work repeated the other identifier at the same source.");
}

static async Task OrdinaryRetryIsNotInteractive()
{
    await using var fixture = await Fixture.Create();
    var original = Started(fixture.Service.TryStart("owner", Work(1)));
    (await fixture.Checker.Next()).Finish(AccountStatus.Blocked);
    await fixture.Finished("owner", original.Id);
    var manual = Started(fixture.Service.TryStartManual("owner", original.Id, 1, "test-identifier"));
    var interactive = await fixture.Checker.Next();
    Assert(interactive.IsInteractive, "The explicitly requested manual check was not interactive.");
    interactive.Finish(AccountStatus.Unconfirmed);
    await fixture.Finished("owner", manual.Id);
    var retry = Started(fixture.Service.Retry("owner", manual.Id));
    Assert(!retry.IsInteractive && retry.Total == 1, "Ordinary retry retained the interactive flag.");
    var normal = await fixture.Checker.Next();
    Assert(!normal.IsInteractive && normal.Source.Id == 1 && fixture.Checker.InteractiveCallCount == 1,
        "Ordinary retry opened a second interactive check without an explicit manual request.");
    normal.Finish(AccountStatus.Found);
    await fixture.Finished("owner", retry.Id);
}

static async Task DeclarationMetadataRoundtrip()
{
    await using var fixture = await Fixture.Create();
    const string identifier = "Тестовий Користувач";
    const string secret = "TEST-ONLY-declaration-source-key-640723";
    var declarations = new[]
    {
        new DeclarationSummary("11111111-1111-4111-8111-111111111111", identifier, "2025", "Щорічна", "Декларація",
            "Тестова посада", "Тестова установа", new DateTimeOffset(2026, 3, 1, 10, 20, 30, TimeSpan.Zero),
            "https://public.nazk.gov.ua/documents/11111111-1111-4111-8111-111111111111"),
        new DeclarationSummary("22222222-2222-4222-8222-222222222222", identifier, "2024", null, null,
            null, null, null, "https://public.nazk.gov.ua/documents/22222222-2222-4222-8222-222222222222")
    };
    var official = new MediaSource
    {
        Id = 20, Name = "НАЗК — декларації", LinkType = "FullName", ErrorKeywordsRaw = "[]",
        ApiUrl = "https://public-api.nazk.gov.ua/v2/documents/list?query={0}",
        SearchUrl = "https://public.nazk.gov.ua/documents?query={0}", ApiKey = secret
    };
    var other = Work(1)[0];
    var original = Started(fixture.Service.TryStart("owner", new[] { new SearchWorkItem(official, identifier), other }));
    var declarationCall = await fixture.Checker.Next();
    Assert(declarationCall.Source.Id == 20 && declarationCall.Source.LinkType == "FullName" && !declarationCall.IsInteractive,
        "The declaration source was not checked through the normal full-name work item.");
    declarationCall.Response.TrySetResult(new AccountCheckResult(AccountStatus.Found, "Знайдено декларації в офіційному реєстрі.",
        "Детермінована відповідь локального тесту.", declarations, 3));
    var unresolved = await fixture.Checker.Next();
    var progress = fixture.Service.Get("owner", original.Id)!;
    var declarationRow = progress.Results.Single(row => row.SiteId == 20);
    AssertDeclarationMetadata(declarationRow, declarations, 3);
    AssertDeclarationMetadata(Planned(progress).Single(row => row.SiteId == 20), declarations, 3);
    Assert(progress.Completed == 1 && progress.Total == 2 && progress.Results.Count == 1,
        "Declaration records were counted as separate site checks instead of one source result.");
    unresolved.Finish(AccountStatus.Blocked);
    var completed = await fixture.Finished("owner", original.Id);

    var serialized = JsonSerializer.Serialize(completed);
    Assert(!serialized.Contains(secret, StringComparison.Ordinal) &&
        !serialized.Contains("public-api.nazk.gov.ua", StringComparison.Ordinal) &&
        !serialized.Contains("ApiKey", StringComparison.OrdinalIgnoreCase), "Declaration progress exposed source credentials or private API configuration.");
    var restored = JsonSerializer.Deserialize<SearchJobSnapshot>(serialized)!;
    AssertDeclarationMetadata(restored.Results.Single(row => row.SiteId == 20), declarations, 3);
    Assert(restored.Results.Single(row => row.SiteId == 20).CheckedAt == declarationRow.CheckedAt,
        "The declaration check timestamp changed during the progress JSON roundtrip.");

    var session = new MemorySession();
    var accessor = new HttpContextAccessor { HttpContext = new DefaultHttpContext { Session = session } };
    new ResultsRepository(accessor).SaveJob(completed);
    var history = new ResultsRepository(accessor).GetListResults();
    var stored = history.Single(result => result.Nickname == identifier).Details.Single();
    AssertDeclarationMetadata(stored, declarations, 3);
    AssertDeclarationMetadata(new ResultsRepository(accessor).GetListLustResults()
        .Single(result => result.Nickname == identifier).Details.Single(), declarations, 3);
    Assert(!session.GetString("UserResults")!.Contains(secret, StringComparison.Ordinal), "Saved declaration history exposed a source API key.");

    var retry = Started(fixture.Service.Retry("owner", completed.Id));
    Assert(retry.Total == 1, "Retry included a confirmed declaration source result.");
    var retried = await fixture.Checker.Next();
    Assert(retried.Source.Id == 1, "Retry called the declaration source despite its Found outcome.");
    retried.Finish(AccountStatus.Blocked);
    await fixture.Finished("owner", retry.Id);

    fixture.Time.Advance(TimeSpan.FromMinutes(1));
    var manual = Started(fixture.Service.TryStartManual("owner", completed.Id, 1, other.Identifier));
    Assert(manual.IsInteractive && manual.Total == 2 && manual.Completed == 1 && manual.Results.Count == 1,
        "Manual admission dropped the declaration result or reset its completed progress.");
    AssertDeclarationMetadata(manual.Results.Single(), declarations, 3);
    Assert(manual.Results.Single().CheckedAt == declarationRow.CheckedAt,
        "Manual admission changed a retained declaration timestamp.");
    var manualCall = await fixture.Checker.Next();
    Assert(manualCall.Source.Id == 1 && manualCall.IsInteractive, "Manual checking reopened the declaration source instead of its selected unresolved row.");
    manualCall.Finish(AccountStatus.Found);
    var manualFinal = await fixture.Finished("owner", manual.Id);
    var retained = manualFinal.Results.Single(row => row.SiteId == 20);
    AssertDeclarationMetadata(retained, declarations, 3);
    Assert(retained.CheckedAt == declarationRow.CheckedAt && fixture.Checker.CallCount == 4 && fixture.Checker.InteractiveCallCount == 1,
        "Manual completion rechecked declarations or rewrote their verification timestamp.");
    Assert(fixture.Service.Retry("owner", manual.Id).StatusCode == 400,
        "A job containing only confirmed results including declarations was retried.");
}

static void AssertDeclarationMetadata(SearchResultItem row, IReadOnlyList<DeclarationSummary> expected, int total)
{
    Assert(row.Outcome == AccountStatus.Found && row.DeclarationTotal == total && row.Declarations != null &&
        row.Declarations.SequenceEqual(expected), "Typed declaration metadata, null fields, ordering, or total count changed.");
}

static Task DeclarationExcelExport()
{
    var declarations = new[]
    {
        new DeclarationSummary("11111111-1111-4111-8111-111111111111", "=Тестовий ПІБ & <Петро>", "2025/2026", "Щорічна", "Декларація",
            "=HYPERLINK(\"https://example.test\")", "Установа & <філія>\u0001 😀",
            new DateTimeOffset(2026, 3, 1, 12, 20, 30, TimeSpan.FromHours(2)),
            "https://public.nazk.gov.ua/documents/11111111-1111-4111-8111-111111111111?a=1&b=2"),
        new DeclarationSummary("22222222-2222-4222-8222-222222222222", "Інший Тестовий ПІБ", null, null, null, null, null, null,
            "https://public.nazk.gov.ua/documents/22222222-2222-4222-8222-222222222222")
    };
    var detail = new SearchResultItem(20, "НАЗК — декларації", "=1+1", "https://public.nazk.gov.ua/documents?query=test",
        AccountStatus.Found, "Знайдено декларації", "Є збіги", "Офіційний реєстр",
        new DateTimeOffset(2026, 10, 25, 11, 0, 0, TimeSpan.FromHours(2)), declarations, 2);
    var bytes = ExcelExport.GenerateExcelFile(new()
    {
        new() { Nickname = detail.Identifier, Details = new() { detail }, Results = new() { [detail.Site] = ("legacy duplicate", detail.ProfileUrl) } },
        new() { Nickname = "=SUM(1,2)", Results = new() { ["Legacy & <source>"] = ("Попередній статус", "https://example.test/legacy") } }
    });
    using var stream = new MemoryStream(bytes);
    using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
    var documents = new Dictionary<string, XDocument>();
    foreach (var entry in archive.Entries)
    {
        using var xml = entry.Open();
        documents.Add(entry.FullName, XDocument.Load(xml));
    }
    XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    XNamespace package = "http://schemas.openxmlformats.org/package/2006/relationships";
    XNamespace contentTypes = "http://schemas.openxmlformats.org/package/2006/content-types";
    Assert(documents["xl/workbook.xml"].Descendants(ns + "sheet").Select(sheet => (string?)sheet.Attribute("name"))
        .SequenceEqual(new[] { "Results", "Declarations" }), "The workbook did not register its Results and Declarations worksheets.");
    Assert(documents["xl/_rels/workbook.xml.rels"].Descendants(package + "Relationship").Any(relationship =>
        (string?)relationship.Attribute("Id") == "rId2" && (string?)relationship.Attribute("Target") == "worksheets/sheet2.xml"),
        "The declarations worksheet relationship is missing.");
    Assert(documents["[Content_Types].xml"].Descendants(contentTypes + "Override").Any(part =>
        (string?)part.Attribute("PartName") == "/xl/worksheets/sheet2.xml" &&
        (string?)part.Attribute("ContentType") == "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"),
        "The declarations worksheet content type is missing.");
    var resultRows = documents["xl/worksheets/sheet1.xml"].Descendants(ns + "row").ToArray();
    Assert(resultRows.Length == 3 && resultRows.All(row => row.Elements(ns + "c").Count() == 5),
        "Rich metadata changed the five legacy result columns or duplicated a typed result.");
    Assert(resultRows[1].Descendants(ns + "t").Any(value => value.Value == "2026-10-25 09:00 UTC"),
        "A typed result timestamp labeled UTC was not converted to UTC.");
    var rows = documents["xl/worksheets/sheet2.xml"].Descendants(ns + "row").ToArray();
    Assert(rows.Length == 3 && rows.All(row => row.Elements(ns + "c").Count() == 9),
        "Declaration export omitted a record, a metadata column, or its header.");
    var first = rows[1].Descendants(ns + "t").Select(text => text.Value).ToArray();
    Assert(first.SequenceEqual(new[]
    {
        "=1+1", declarations[0].FullName, "2025/2026", "Щорічна", "Декларація", declarations[0].Position!,
        "Установа & <філія> 😀", "2026-03-01 10:20:30 UTC", declarations[0].Url
    }), "Declaration metadata, XML escaping, Unicode, UTC submission time, or literal formula-like strings changed.");
    var second = rows[2].Descendants(ns + "t").Select(text => text.Value).ToArray();
    Assert(second.Skip(2).Take(6).All(string.IsNullOrEmpty), "Missing declaration fields were not exported as empty cells.");
    Assert(documents.Values.All(document => !document.Descendants(ns + "f").Any()) &&
        documents["xl/worksheets/sheet2.xml"].Descendants(ns + "c").All(cell => (string?)cell.Attribute("t") == "inlineStr"),
        "Untrusted declaration text became an executable spreadsheet formula.");

    using var emptyStream = new MemoryStream(ExcelExport.GenerateExcelFile(new()));
    using var emptyArchive = new ZipArchive(emptyStream, ZipArchiveMode.Read);
    Assert(emptyArchive.GetEntry("xl/worksheets/sheet2.xml") == null,
        "An export without declaration metadata gained an unnecessary extra worksheet.");
    return Task.CompletedTask;
}

static async Task YouControlSearchRoundtrip()
{
    await using var fixture = await Fixture.Create();
    const string identifier = "Тестовий Користувач";
    const string secret = "TEST-ONLY-private-source-query";
    var source = new MediaSource { Id = 21, Name = YouControlDeclarationSource.Name, LinkType = "FullName",
        SearchUrl = YouControlDeclarationSource.SearchTemplate + "&token=" + secret, ApiKey = secret };
    var original = Started(fixture.Service.TryStart("owner", new[] { new SearchWorkItem(source, identifier), Work(1)[0] }));
    var prepared = await fixture.Checker.Next();
    prepared.Response.TrySetResult(YouControlDeclarationSource.PrepareSearch(identifier, CancellationToken.None));
    var other = await fixture.Checker.Next();
    other.Finish(AccountStatus.Blocked);
    var finished = await fixture.Finished("owner", original.Id);
    var row = finished.Results.Single(item => item.SiteId == 21);
    Assert(row.ProfileUrl == YouControlDeclarationSource.SearchUrl(identifier) && row.Outcome == AccountStatus.Unconfirmed &&
        row.Declarations == null && row.DeclarationTotal == null && finished.CanRetry,
        "The handoff did not retain its public search URL or invented a result inventory.");
    Assert(!JsonSerializer.Serialize(finished).Contains(secret), "The public search URL leaked source credentials or private parameters.");
    var session = new MemorySession();
    var accessor = new HttpContextAccessor { HttpContext = new DefaultHttpContext { Session = session } };
    var repository = new ResultsRepository(accessor);
    repository.SaveJob(finished);
    var history = repository.GetListResults();
    var restored = history.SelectMany(item => item.Details).Single(item => item.SiteId == 21);
    Assert(restored.ProfileUrl == row.ProfileUrl && restored.Declarations == null && restored.DeclarationTotal == null,
        "History changed the prepared search into an absent or found declaration result.");
    using var stream = new MemoryStream(ExcelExport.GenerateExcelFile(history));
    using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
    using var reader = new StreamReader(archive.GetEntry("xl/worksheets/sheet1.xml")!.Open());
    Assert((await reader.ReadToEndAsync()).Contains("youcontrol.com.ua/catalog/individuals/search/") &&
        archive.GetEntry("xl/worksheets/sheet2.xml") == null, "Export omitted the search URL or invented declaration rows.");
    Assert(fixture.Service.TryStartManual("owner", finished.Id, 21, identifier).StatusCode == 400,
        "The handoff opened the automated manual Chrome flow.");
    var retry = Started(fixture.Service.Retry("owner", finished.Id));
    Assert(retry.Total == 1, "The handoff was retried despite having no automated lookup.");
    var call = await fixture.Checker.Next();
    Assert(call.Source.Id == 1, "Retry chose the external catalog instead of the failed provider.");
    call.Finish(AccountStatus.Found);
    await fixture.Finished("owner", retry.Id);
    var single = Started(fixture.Service.TryStart("owner", new[] { new SearchWorkItem(source, identifier) }));
    (await fixture.Checker.Next()).Response.TrySetResult(YouControlDeclarationSource.PrepareSearch(identifier, CancellationToken.None));
    var onlyHandoff = await fixture.Finished("owner", single.Id);
    Assert(!onlyHandoff.CanRetry && fixture.Service.Retry("owner", single.Id).StatusCode == 400,
        "An external search offered a meaningless automatic retry.");
}

static IReadOnlyList<SearchResultItem> Planned(SearchJobSnapshot snapshot) => snapshot.PlannedResults
    ?? throw new InvalidOperationException("The job snapshot did not include planned source rows.");

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

    private Fixture(int workerCount, int queueCapacity, int maxRetainedJobs, TimeSpan retention, CaptchaOptions? captchaOptions)
    {
        Service = new SearchJobService(Checker, Options.Create(new SearchJobOptions
        {
            WorkerCount = workerCount, QueueCapacity = queueCapacity,
            MaxRetainedJobs = maxRetainedJobs, Retention = retention
        }), Time, NullLogger<SearchJobService>.Instance, Options.Create(captchaOptions ?? new CaptchaOptions()));
    }

    public static async Task<Fixture> Create(int workerCount = 1, int queueCapacity = 8,
        int maxRetainedJobs = 100, TimeSpan? retention = null, CaptchaOptions? captchaOptions = null)
    {
        var fixture = new Fixture(workerCount, queueCapacity, maxRetainedJobs, retention ?? TimeSpan.FromHours(2), captchaOptions);
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
    private int _interactiveCallCount;
    public int Active => Volatile.Read(ref _active);
    public int MaxActive => Volatile.Read(ref _maxActive);
    public int CallCount => Volatile.Read(ref _callCount);
    public int InteractiveCallCount => Volatile.Read(ref _interactiveCallCount);

    public Task<AccountCheckResult> CheckAsync(MediaSource source, string identifier, CancellationToken cancellationToken) =>
        Check(source, identifier, cancellationToken, false);

    public Task<AccountCheckResult> CheckInteractiveAsync(MediaSource source, string identifier, CancellationToken cancellationToken) =>
        Check(source, identifier, cancellationToken, true);

    public Task<AccountCheckResult> CheckWithCaptchaAsync(MediaSource source, string identifier, CaptchaSolveBudget budget,
        Action<string> onProgress, CancellationToken cancellationToken) => Check(source, identifier, cancellationToken, false, budget, onProgress);

    private async Task<AccountCheckResult> Check(MediaSource source, string identifier, CancellationToken cancellationToken, bool interactive,
        CaptchaSolveBudget? captchaBudget = null, Action<string>? onProgress = null)
    {
        var active = Interlocked.Increment(ref _active);
        int observed;
        do
        {
            observed = Volatile.Read(ref _maxActive);
            if (observed >= active) break;
        } while (Interlocked.CompareExchange(ref _maxActive, active, observed) != observed);
        Interlocked.Increment(ref _callCount);
        if (interactive) Interlocked.Increment(ref _interactiveCallCount);
        var invocation = new Invocation(source, identifier, cancellationToken, interactive, captchaBudget, onProgress);
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

sealed class Invocation(MediaSource source, string identifier, CancellationToken cancellationToken, bool interactive = false,
    CaptchaSolveBudget? captchaBudget = null, Action<string>? onProgress = null)
{
    public MediaSource Source { get; } = source;
    public string Identifier { get; } = identifier;
    public CancellationToken CancellationToken { get; } = cancellationToken;
    public bool IsInteractive { get; } = interactive;
    public CaptchaSolveBudget? CaptchaBudget { get; } = captchaBudget;
    public Action<string>? ReportProgress { get; } = onProgress;
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
