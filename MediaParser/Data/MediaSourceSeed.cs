using System.Text.Json;
using MediaParser.Models.DBModels;
using Microsoft.EntityFrameworkCore;

namespace MediaParser.Data;

public static class MediaSourceSeed
{
    public static async Task InitializeAsync(AppDBContext database)
    {
        if (await database.MediaSources.AnyAsync())
            return;

        var path = Path.Combine(AppContext.BaseDirectory, "Data", "media-sources.json");
        var sources = JsonSerializer.Deserialize<List<MediaSource>>(await File.ReadAllTextAsync(path))
            ?? throw new InvalidOperationException("The default media source list is empty.");
        database.MediaSources.AddRange(sources);
        await database.SaveChangesAsync();
    }
}
