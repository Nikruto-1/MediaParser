using MediaParser.Models.DBModels;

namespace MediaParser.Models;

public sealed class SearchRequest
{
    public List<string> Identifiers { get; set; } = new();
    public string Mode { get; set; } = "User";
    public List<int> SourceIds { get; set; } = new();
}

// Work items stay on the server; source credentials never enter a progress response.
public sealed record SearchWorkItem(MediaSource Source, string Identifier, bool Interactive = false);

public sealed class ManualCheckRequest
{
    public int SiteId { get; set; }
    public string Identifier { get; set; } = string.Empty;
}

public sealed record SearchResultItem(int SiteId, string Site, string Identifier, string ProfileUrl,
    AccountStatus Outcome, string Label, string Reason, string? Evidence, DateTimeOffset? CheckedAt)
{
    public string Status => Outcome.ToString().ToLowerInvariant();
}

public sealed record SearchJobSnapshot(Guid Id, string State, int Total, int Completed,
    string? CurrentSite, string? CurrentIdentifier, IReadOnlyList<SearchResultItem> Results,
    bool CanRetry, string? Error, bool CancellationRequested, DateTimeOffset StartedAt,
    IReadOnlyList<SearchResultItem>? PlannedResults = null, bool IsInteractive = false);

public sealed record SearchJobStartResult(SearchJobSnapshot? Job, string? Error, int StatusCode);

public sealed class SearchJobOptions
{
    public int WorkerCount { get; set; } = 2;
    public int QueueCapacity { get; set; } = 16;
    public int MaxRetainedJobs { get; set; } = 100;
    public TimeSpan Retention { get; set; } = TimeSpan.FromHours(2);
}
