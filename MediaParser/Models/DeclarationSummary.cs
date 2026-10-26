namespace MediaParser.Models;

// Public search metadata only. Full declarations and private fields are not stored by this application.
public sealed record DeclarationSummary(string Id, string FullName, string? DeclarationYear,
    string? DeclarationType, string? DocumentType, string? Position, string? Workplace,
    DateTimeOffset? SubmittedAt, string Url, string? SubmittedDate = null);
