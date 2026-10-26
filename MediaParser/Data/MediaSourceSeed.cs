using System.Text.Json;
using MediaParser.Models.DBModels;
using MediaParser.Services.Declarations;
using Microsoft.EntityFrameworkCore;

namespace MediaParser.Data;

public static class MediaSourceSeed
{
    public static async Task InitializeAsync(AppDBContext database)
    {
        if (await database.MediaSources.AnyAsync())
        {
            // Repair only the exact old bundled URL; preserve user edits and credentials.
            var rumble = await database.MediaSources.Where(source => source.Name == "Rumble"
                && source.LinkType == "User" && source.SearchUrl == "https://rumble.com/{0}").ToListAsync();
            foreach (var source in rumble) source.SearchUrl = "https://rumble.com/user/{0}";
            var existing = await database.MediaSources.ToListAsync();
            foreach (var source in existing.Where(source => source.Name == NazkDeclarationSource.Name &&
                source.LinkType == "FullName" && source.SearchUrl == "https://public.nazk.gov.ua/documents/list?query={0}"))
                source.SearchUrl = NazkDeclarationSource.SearchTemplate;
            if (!existing.Any(source => source.Name == NazkDeclarationSource.Name || NazkDeclarationSource.IsSource(source)))
                database.MediaSources.Add(new MediaSource
                {
                    Name = NazkDeclarationSource.Name, LinkType = "FullName",
                    ApiUrl = NazkDeclarationSource.ApiTemplate, SearchUrl = NazkDeclarationSource.SearchTemplate,
                    ErrorKeywordsRaw = "[]"
                });
            if (!existing.Any(source => source.Name == YouControlDeclarationSource.Name || YouControlDeclarationSource.IsSource(source)))
                database.MediaSources.Add(new MediaSource
                {
                    Name = YouControlDeclarationSource.Name, LinkType = "FullName",
                    SearchUrl = YouControlDeclarationSource.SearchTemplate, ErrorKeywordsRaw = "[]"
                });
            await database.SaveChangesAsync();
            return;
        }

        var path = Path.Combine(AppContext.BaseDirectory, "Data", "media-sources.json");
        var sources = JsonSerializer.Deserialize<List<MediaSource>>(await File.ReadAllTextAsync(path))
            ?? throw new InvalidOperationException("The default media source list is empty.");
        database.MediaSources.AddRange(sources);
        await database.SaveChangesAsync();
    }
}
