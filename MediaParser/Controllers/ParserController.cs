using MediaParser.Classes;
using MediaParser.Models;
using MediaParser.Repositories;
using MediaParser.Services;
using Microsoft.AspNetCore.Mvc;

namespace MediaParser.Controllers;

public class ParserController : Controller
{
    private const string OwnerKey = "SearchOwner";
    private readonly MediaService _mediaService;
    private readonly ResultsRepository _resultsRepository;
    private readonly SearchJobService _jobs;

    public ParserController(MediaService mediaService, ResultsRepository resultsRepository, SearchJobService jobs)
    {
        _mediaService = mediaService;
        _resultsRepository = resultsRepository;
        _jobs = jobs;
    }

    public IActionResult MyResults()
    {
        var owner = HttpContext.Session.GetString(OwnerKey);
        if (owner != null)
            foreach (var job in _jobs.GetOwned(owner)) _resultsRepository.SaveJob(job);
        return View(_resultsRepository.GetListResults());
    }

    [HttpGet]
    public async Task<IActionResult> SearchByMedia()
    {
        await _mediaService.UpdateMedia();
        var owner = HttpContext.Session.GetString(OwnerKey);
        ViewData["ActiveJobId"] = owner == null ? null : _jobs.GetOwned(owner)
            .LastOrDefault(job => job.State is "queued" or "running")?.Id;
        return View(new SearchIdentifiers());
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> StartSearch([FromBody] SearchRequest? request)
    {
        if (!ModelState.IsValid || request == null)
            return BadRequest(new { error = "Неправильні дані пошуку. Перевірте ідентифікатори та джерела." });
        return StartResponse(await StartCore(request));
    }

    [HttpGet]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public IActionResult SearchProgress(Guid id)
    {
        var job = OwnedJob(id);
        if (job == null) return MissingJob();
        _resultsRepository.SaveJob(job);
        return Json(job);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult CancelSearch(Guid id)
    {
        var owner = HttpContext.Session.GetString(OwnerKey);
        var job = owner == null ? null : _jobs.Cancel(owner, id);
        if (job == null) return MissingJob();
        _resultsRepository.SaveJob(job);
        return Json(job);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult RetrySearch(Guid id)
    {
        var owner = HttpContext.Session.GetString(OwnerKey);
        if (owner == null) return MissingJob();
        var original = _jobs.Get(owner, id);
        if (original == null) return MissingJob();
        _resultsRepository.SaveJob(original);
        return StartResponse(_jobs.Retry(owner, id));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult ManualCheck(Guid id, [FromBody] ManualCheckRequest? request)
    {
        var owner = HttpContext.Session.GetString(OwnerKey);
        var original = owner == null ? null : _jobs.Get(owner, id);
        if (original == null) return MissingJob();
        if (!ModelState.IsValid || request == null || string.IsNullOrWhiteSpace(request.Identifier) || request.Identifier.Length > 100)
            return BadRequest(new { error = "Оберіть результат для ручної перевірки." });
        _resultsRepository.SaveJob(original);
        return StartResponse(_jobs.TryStartManual(owner!, id, request.SiteId, request.Identifier));
    }

    [HttpGet]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public IActionResult ExportJob(Guid id)
    {
        var job = OwnedJob(id);
        if (job == null) return MissingJob();
        _resultsRepository.SaveJob(job);
        return File(ExcelExport.GenerateExcelFile(ResultsRepository.ToResults(job)),
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"results-{id:N}.xlsx");
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult ExportExcel(bool isExportLastSearch) => File(
        ExcelExport.GenerateExcelFile(isExportLastSearch
            ? _resultsRepository.GetListLustResults() : _resultsRepository.GetListResults()),
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "results.xlsx");

    // A non-JavaScript form uses the same bounded queue and displays its final history page.
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SearchByMedia(SearchIdentifiers identifiers, string mode = "User", List<int>? sourceIds = null)
    {
        await _mediaService.UpdateMedia();
        var start = await StartCore(new SearchRequest
        {
            Identifiers = identifiers.Nicknames,
            Mode = mode,
            SourceIds = sourceIds ?? _mediaService.GetSelectedMedia().Select(source => source.Id).ToList()
        });
        if (start.Job == null)
        {
            ModelState.AddModelError(string.Empty, start.Error ?? "Не вдалося запустити пошук.");
            return View(identifiers);
        }
        try
        {
            var job = start.Job;
            while (job.State is "queued" or "running")
            {
                await Task.Delay(250, HttpContext.RequestAborted);
                job = OwnedJob(job.Id) ?? throw new InvalidOperationException("Search expired while running.");
            }
            _resultsRepository.SaveJob(job);
            return RedirectToAction(nameof(MyResults));
        }
        catch (OperationCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested)
        {
            var owner = HttpContext.Session.GetString(OwnerKey);
            if (owner != null) _jobs.Cancel(owner, start.Job.Id);
            return new EmptyResult();
        }
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SelectMedia(List<string>? selectedMedia)
    {
        await _mediaService.UpdateMedia();
        _mediaService.SetSelectedMedia(selectedMedia);
        return Json(new { success = true });
    }

    private async Task<SearchJobStartResult> StartCore(SearchRequest request)
    {
        if (request.Mode is not ("User" or "FullName"))
            return Invalid("Оберіть режим «Нікнейм» або «Ім’я та прізвище».");
        if (request.Identifiers == null || request.Identifiers.Count is < 1 or > 5 ||
            request.Identifiers.Any(value => string.IsNullOrWhiteSpace(value) || value.Trim().Length > 100))
            return Invalid("Вкажіть від 1 до 5 непорожніх ідентифікаторів, до 100 символів кожен.");
        var identifiers = request.Identifiers.Select(value => value.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (request.Mode == "User" && identifiers.Any(value => value.Any(char.IsWhiteSpace)))
            return Invalid("Нікнейм не може містити пробілів. Для імені та прізвища змініть режим пошуку.");
        if (request.SourceIds == null || request.SourceIds.Count is < 1 or > 50)
            return Invalid("Оберіть хоча б одне джерело для пошуку.");
        await _mediaService.UpdateMedia();
        var sourceIds = request.SourceIds.Distinct().ToHashSet();
        var sources = _mediaService.GetMediaSources().Where(source => sourceIds.Contains(source.Media.Id))
            .Select(source => source.Media).ToList();
        if (sources.Count != sourceIds.Count)
            return Invalid("Частину джерел видалено або змінено. Оновіть список і спробуйте ще раз.");
        if (sources.Any(source => source.LinkType != request.Mode))
            return Invalid("Обрані джерела не відповідають режиму пошуку. Перевірте вибір.");

        var owner = HttpContext.Session.GetString(OwnerKey);
        if (owner == null)
        {
            owner = Guid.NewGuid().ToString("N");
            HttpContext.Session.SetString(OwnerKey, owner);
        }
        var started = _jobs.TryStart(owner, identifiers.SelectMany(identifier => sources
            .Select(source => new SearchWorkItem(source, identifier))).ToList());
        if (started.Job != null) _mediaService.SetSelectedIds(sourceIds);
        return started;
    }

    private SearchJobSnapshot? OwnedJob(Guid id)
    {
        var owner = HttpContext.Session.GetString(OwnerKey);
        return owner == null ? null : _jobs.Get(owner, id);
    }

    private IActionResult StartResponse(SearchJobStartResult result) =>
        result.Job != null ? StatusCode(result.StatusCode, result.Job) : StatusCode(result.StatusCode, new { error = result.Error });

    private NotFoundObjectResult MissingJob() => NotFound(new { error = "Пошук не знайдено або термін його зберігання минув. Запустіть новий пошук." });
    private static SearchJobStartResult Invalid(string message) => new(null, message, 400);
}
