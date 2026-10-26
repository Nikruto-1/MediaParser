using System.Threading.Channels;
using MediaParser.Models;
using MediaParser.Models.DBModels;
using MediaParser.Services.Captcha;
using Microsoft.Extensions.Options;

namespace MediaParser.Services;

public sealed class SearchJobService : BackgroundService
{
    private sealed class Job
    {
        public Guid Id { get; } = Guid.NewGuid();
        public required string Owner { get; init; }
        public required List<SearchWorkItem> Items { get; init; }
        public required DateTimeOffset StartedAt { get; init; }
        public CancellationTokenSource Cancellation { get; } = new();
        public List<SearchResultItem> Results { get; } = new();
        public string State { get; set; } = "queued";
        public int Completed { get; set; }
        public string? CurrentSite { get; set; }
        public int? CurrentSiteId { get; set; }
        public string? CurrentIdentifier { get; set; }
        public string? Error { get; set; }
        public DateTimeOffset? FinishedAt { get; set; }
        public bool WorkerActive { get; set; }
        public bool AutomaticCaptcha { get; init; }
        public CaptchaSolveBudget? CaptchaBudget { get; init; }
        public string? ProgressMessage { get; set; }
    }

    private readonly object _gate = new();
    private readonly Dictionary<Guid, Job> _jobs = new();
    private readonly Channel<Job> _queue;
    private readonly IAccountChecker _checker;
    private readonly SearchJobOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<SearchJobService> _logger;
    private readonly CaptchaOptions _captchaOptions;
    private bool _accepting = true;

    public bool CaptchaEnabled => _captchaOptions.Enabled;
    public bool CaptchaAvailable => _captchaOptions.Enabled && !string.IsNullOrWhiteSpace(_captchaOptions.ApiKey);
    public int CaptchaTaskLimit => _captchaOptions.MaxTasksPerSearch;

    public SearchJobService(IAccountChecker checker, IOptions<SearchJobOptions> options,
        TimeProvider timeProvider, ILogger<SearchJobService> logger, IOptions<CaptchaOptions>? captchaOptions = null)
    {
        _checker = checker;
        _options = options.Value;
        _clock = timeProvider;
        _logger = logger;
        _captchaOptions = captchaOptions?.Value ?? new CaptchaOptions();
        if (_options.WorkerCount is < 1 or > 4 || _options.QueueCapacity < 1 ||
            _options.MaxRetainedJobs < 1 || _options.Retention <= TimeSpan.Zero)
            throw new ArgumentException("Invalid search job limits.", nameof(options));
        _queue = Channel.CreateBounded<Job>(new BoundedChannelOptions(_options.QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = _options.WorkerCount == 1,
            SingleWriter = false
        });
    }

    public SearchJobStartResult TryStart(string owner, IReadOnlyList<SearchWorkItem> items, bool automaticCaptcha = false) =>
        TryStartCore(owner, items, Array.Empty<SearchResultItem>(), automaticCaptcha);

    private SearchJobStartResult TryStartCore(string owner, IReadOnlyList<SearchWorkItem> items,
        IReadOnlyList<SearchResultItem> retainedResults, bool automaticCaptcha = false)
    {
        lock (_gate)
        {
            PruneExpiredCore();
            if (string.IsNullOrWhiteSpace(owner) || items.Count is < 1 or > 250 ||
                items.Any(item => string.IsNullOrWhiteSpace(item.Identifier) || item.Identifier.Length > 100))
                return new(null, "Вкажіть від 1 до 5 ідентифікаторів і виберіть джерела.", 400);
            if (!_accepting)
                return new(null, "Сервер зупиняється. Спробуйте пізніше.", 503);
            if (automaticCaptcha && !CaptchaAvailable)
                return new(null, "Автоматична капча не налаштована. Додайте локальний API-ключ 2Captcha та увімкніть інтеграцію.", 400);
            if (_jobs.Values.Any(job => job.Owner == owner && !IsTerminal(job)))
                return new(null, "Пошук уже виконується. Дочекайтеся завершення або зупиніть його.", 409);

            // Evict only finished jobs when the retention budget is full.
            while (_jobs.Count >= _options.MaxRetainedJobs)
            {
                var oldest = _jobs.Values.Where(job => IsTerminal(job) && !job.WorkerActive)
                    .OrderBy(job => job.FinishedAt).FirstOrDefault();
                if (oldest == null)
                    return new(null, "Усі місця пошуку зайняті. Спробуйте трохи пізніше.", 429);
                RemoveCore(oldest);
            }

            var job = new Job
            {
                Owner = owner,
                StartedAt = _clock.GetUtcNow(),
                Items = items.GroupBy(item => (item.Source.Id, item.Identifier))
                    .Select(group => new SearchWorkItem(CloneSource(group.First().Source), group.Key.Identifier, group.First().Interactive)).ToList(),
                Completed = retainedResults.Count,
                AutomaticCaptcha = automaticCaptcha,
                CaptchaBudget = automaticCaptcha ? new CaptchaSolveBudget(_captchaOptions.MaxTasksPerSearch) : null
            };
            job.Results.AddRange(retainedResults);
            if (!_queue.Writer.TryWrite(job))
            {
                job.Cancellation.Dispose();
                return new(null, "Черга пошуку заповнена. Спробуйте трохи пізніше.", 429);
            }
            _jobs.Add(job.Id, job);
            return new(Snapshot(job), null, 202);
        }
    }

    public SearchJobSnapshot? Get(string owner, Guid id)
    {
        lock (_gate)
        {
            PruneExpiredCore();
            return FindCore(owner, id) is { } job ? Snapshot(job) : null;
        }
    }

    public IReadOnlyList<SearchJobSnapshot> GetOwned(string owner)
    {
        lock (_gate)
        {
            PruneExpiredCore();
            return _jobs.Values.Where(job => job.Owner == owner).OrderBy(job => job.StartedAt)
                .Select(Snapshot).ToArray();
        }
    }

    public SearchJobSnapshot? Cancel(string owner, Guid id)
    {
        Job? job;
        SearchJobSnapshot snapshot;
        lock (_gate)
        {
            job = FindCore(owner, id);
            if (job == null) return null;
            if (!IsTerminal(job)) CancelCore(job);
            snapshot = Snapshot(job);
        }
        // Keep callbacks outside the state lock. Late browser responses cannot replace a cancelled snapshot.
        try { job.Cancellation.Cancel(); }
        catch (ObjectDisposedException) { }
        return snapshot;
    }

    public SearchJobStartResult Retry(string owner, Guid id)
    {
        lock (_gate)
        {
            var job = FindCore(owner, id);
            if (job == null) return new(null, "Пошук не знайдено або термін його зберігання минув.", 404);
            if (!IsTerminal(job)) return new(null, "Спочатку завершіть або зупиніть поточний пошук.", 409);
            var retry = job.Items.Where(item => CanRetryItem(job, item))
                .Select(item => new SearchWorkItem(item.Source, item.Identifier)).ToList();
            if (retry.Count == 0) return new(null, "Немає невдалих перевірок для повтору.", 400);
            return TryStart(owner, retry, job.AutomaticCaptcha);
        }
    }

    public SearchJobStartResult TryStartManual(string owner, Guid id, int siteId, string identifier)
    {
        lock (_gate)
        {
            var original = FindCore(owner, id);
            if (original == null) return new(null, "Пошук не знайдено або термін його зберігання минув.", 404);
            if (!IsTerminal(original)) return new(null, "Дочекайтеся завершення або зупиніть поточний пошук.", 409);
            var item = original.Items.FirstOrDefault(value => value.Source.Id == siteId && value.Identifier == identifier);
            var result = original.Results.FirstOrDefault(value => value.SiteId == siteId && value.Identifier == identifier);
            if (item == null || result == null || result.Outcome is AccountStatus.Found or AccountStatus.NotFound)
                return new(null, "Оберіть непідтверджений результат, обмеження доступу або помилку для ручної перевірки.", 400);
            if (item.Source.LinkType == "FullName")
                return new(null, "Список результатів за ПІБ перевірте вручну за посиланням на джерело.", 400);
            if (string.IsNullOrWhiteSpace(item.Source.SearchUrl))
                return new(null, "У джерела немає публічної адреси профілю для відкриття у Chrome.", 400);
            if (!Classes.SourceCapabilities.SupportsPublicProfileLookup(item.Source.SearchUrl))
                return new(null, "Це джерело не має публічного пошуку профілю за цим URL. Перегляньте підказку джерела.", 400);
            try { Classes.SearchAlgorithm.BuildSearchUrl(item.Source.SearchUrl, identifier); }
            catch (Exception exception) when (exception is ArgumentException or FormatException)
            { return new(null, "У джерела неправильна адреса профілю. Виправте його налаштування.", 400); }
            if (_jobs.Values.Any(job => job.Items.Any(value => value.Interactive) && (!IsTerminal(job) || job.WorkerActive)))
                return new(null, "Ручна перевірка вже відкрита у Chrome. Завершіть або зупиніть її.", 429);

            var retained = original.Results.Where(value => value.SiteId != siteId || value.Identifier != identifier).ToArray();
            var items = original.Items.Select(value =>
            {
                var source = CloneSource(value.Source);
                bool interactive = source.Id == siteId && value.Identifier == identifier;
                if (interactive) { source.ApiUrl = null; source.ApiKey = null; }
                return new SearchWorkItem(source, value.Identifier, interactive);
            }).ToArray();
            // Other results remain visible and keep their original verification times.
            return TryStartCore(owner, items, retained);
        }
    }

    public int PruneExpired()
    {
        lock (_gate) return PruneExpiredCore();
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.WhenAll(Enumerable.Range(0, _options.WorkerCount).Select(_ => RunWorkerAsync(stoppingToken)));

    private async Task RunWorkerAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var job in _queue.Reader.ReadAllAsync(stoppingToken))
            {
                lock (_gate)
                {
                    if (!_jobs.ContainsKey(job.Id) || IsTerminal(job)) continue;
                    job.State = "running";
                    job.WorkerActive = true;
                }
                using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, job.Cancellation.Token);
                try
                {
                    foreach (var item in job.Items)
                    {
                        cancellation.Token.ThrowIfCancellationRequested();
                        lock (_gate)
                        {
                            if (IsTerminal(job)) break;
                            if (job.Results.Any(result => result.SiteId == item.Source.Id && result.Identifier == item.Identifier)) continue;
                            job.CurrentSite = item.Source.Name;
                            job.CurrentSiteId = item.Source.Id;
                            job.CurrentIdentifier = item.Identifier;
                            job.ProgressMessage = null;
                        }
                        AccountCheckResult check;
                        try
                        {
                            if (item.Interactive)
                                check = await _checker.CheckInteractiveAsync(item.Source, item.Identifier, cancellation.Token);
                            else if (job.CaptchaBudget != null)
                                check = await _checker.CheckWithCaptchaAsync(item.Source, item.Identifier, job.CaptchaBudget, message =>
                                {
                                    lock (_gate) { if (!IsTerminal(job)) job.ProgressMessage = message; }
                                }, cancellation.Token);
                            else
                                check = await _checker.CheckAsync(item.Source, item.Identifier, cancellation.Token);
                        }
                        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { throw; }
                        catch (Exception exception)
                        {
                            _logger.LogWarning("Source {SiteId} check failed ({ExceptionType}).", item.Source.Id, exception.GetType().Name);
                            check = new(AccountStatus.Error, "Не вдалося перевірити джерело. Спробуйте ще раз пізніше.");
                        }
                        lock (_gate)
                        {
                            if (IsTerminal(job)) break;
                            job.Results.Add(MakeResult(item, check));
                            job.Completed++;
                        }
                    }
                    lock (_gate)
                    {
                        if (!IsTerminal(job)) FinishCore(job, "completed");
                    }
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                {
                    lock (_gate) { if (!IsTerminal(job)) CancelCore(job); }
                }
                catch (Exception exception)
                {
                    _logger.LogError("Search worker failed ({ExceptionType}).", exception.GetType().Name);
                    lock (_gate)
                    {
                        if (!IsTerminal(job))
                        {
                            job.Error = "Пошук перервано через помилку сервера. Готові результати збережено.";
                            FinishCore(job, "failed");
                        }
                    }
                }
                finally
                {
                    lock (_gate) job.WorkerActive = false;
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        List<Job> active;
        lock (_gate)
        {
            _accepting = false;
            active = _jobs.Values.Where(job => !IsTerminal(job)).ToList();
            foreach (var job in active) CancelCore(job);
            _queue.Writer.TryComplete();
        }
        foreach (var job in active) job.Cancellation.Cancel();
        await base.StopAsync(cancellationToken);
    }

    private Job? FindCore(string owner, Guid id)
    {
        PruneExpiredCore();
        return _jobs.TryGetValue(id, out var job) && job.Owner == owner ? job : null;
    }

    private void CancelCore(Job job)
    {
        foreach (var item in job.Items.Where(item => !job.Results.Any(result =>
            result.SiteId == item.Source.Id && result.Identifier == item.Identifier)))
            job.Results.Add(MakeResult(item, new(AccountStatus.Cancelled, "Перевірку зупинено; існування профілю не визначено.")));
        FinishCore(job, "cancelled");
    }

    private void FinishCore(Job job, string state)
    {
        job.State = state;
        job.FinishedAt = _clock.GetUtcNow();
        job.CurrentSite = null;
        job.CurrentSiteId = null;
        job.CurrentIdentifier = null;
        job.ProgressMessage = null;
    }

    private SearchResultItem MakeResult(SearchWorkItem item, AccountCheckResult check)
    {
        string profileUrl = string.Empty;
        if (Declarations.NazkDeclarationSource.IsSource(item.Source))
            profileUrl = Declarations.NazkDeclarationSource.SearchUrl(item.Identifier);
        else if (Declarations.YouControlDeclarationSource.IsSource(item.Source))
            profileUrl = Declarations.YouControlDeclarationSource.SearchUrl(item.Identifier);
        // API endpoints may include private query parameters and must not be exposed as profile links.
        else if (!string.IsNullOrWhiteSpace(item.Source.SearchUrl))
        {
            try { profileUrl = Classes.SearchAlgorithm.BuildSearchUrl(item.Source.SearchUrl, item.Identifier); }
            catch (ArgumentException) { }
            catch (FormatException) { }
        }
        return new(item.Source.Id, item.Source.Name, item.Identifier, profileUrl, check.Status,
            check.Label, check.Reason, check.Evidence,
            check.Status is AccountStatus.Pending or AccountStatus.Checking ? null : _clock.GetUtcNow(),
            check.Declarations?.ToArray(), check.DeclarationTotal);
    }

    private static bool IsTerminal(Job job) => job.State is "completed" or "cancelled" or "failed";

    private static bool CanRetryItem(Job job, SearchWorkItem item) =>
        !Declarations.YouControlDeclarationSource.IsSource(item.Source) && !job.Results.Any(result =>
            result.SiteId == item.Source.Id && result.Identifier == item.Identifier &&
            result.Outcome is AccountStatus.Found or AccountStatus.NotFound);

    private SearchJobSnapshot Snapshot(Job job) => new(job.Id, job.State, job.Items.Count, job.Completed,
        job.CurrentSite, job.CurrentIdentifier, job.Results.ToArray(),
        IsTerminal(job) && job.Items.Any(item => CanRetryItem(job, item)),
        job.Error, job.State == "cancelled", job.StartedAt,
        job.Items.Select(item => job.Results.FirstOrDefault(result => result.SiteId == item.Source.Id && result.Identifier == item.Identifier)
            ?? MakeResult(item, job.State == "running" && job.CurrentSiteId == item.Source.Id && job.CurrentIdentifier == item.Identifier
                ? new(AccountStatus.Checking, item.Interactive ? "Очікуємо ручної перевірки у відкритому Chrome." : "Перевіряємо це джерело.")
                : new(AccountStatus.Pending, IsTerminal(job) ? "Перевірка не завершилася. Можна повторити." : "Джерело в черзі; перевірка ще не почалася."))).ToArray(),
        job.Items.Any(item => item.Interactive), job.AutomaticCaptcha, job.ProgressMessage);

    private int PruneExpiredCore()
    {
        var expired = _jobs.Values.Where(job => job.FinishedAt.HasValue && !job.WorkerActive &&
            _clock.GetUtcNow() - job.FinishedAt.Value >= _options.Retention).ToList();
        foreach (var job in expired) RemoveCore(job);
        return expired.Count;
    }

    private void RemoveCore(Job job)
    {
        _jobs.Remove(job.Id);
        job.Cancellation.Dispose();
    }

    private static MediaSource CloneSource(MediaSource source) => new()
    {
        Id = source.Id, Name = source.Name, SearchUrl = source.SearchUrl, ApiUrl = source.ApiUrl,
        ApiKey = source.ApiKey, ErrorKeywordsRaw = source.ErrorKeywordsRaw, LinkType = source.LinkType
    };
}
