using System.Text;

namespace MediaParser.Services.Declarations;

public static class YouControlPageImport
{
    public const int MaxBytes = 2 * 1024 * 1024;

    public static async Task<CatalogPersonPage> ReadAsync(Stream stream, string sourceUrl, CancellationToken cancellationToken)
    {
        if (sourceUrl.Length > 512 || !Uri.TryCreate(sourceUrl, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || !YouControlCatalogParser.TryPersonUrl(sourceUrl, out _))
            throw new InvalidDataException("Вкажіть HTTPS-адресу публічної картки людини на YouControl, до 512 символів.");
        using var content = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
        {
            if (content.Length + read > MaxBytes)
                throw new InvalidDataException("Збережена сторінка має бути розміром до 2 MiB.");
            content.Write(buffer, 0, read);
        }
        cancellationToken.ThrowIfCancellationRequested();
        var parsed = YouControlCatalogParser.ParsePerson(Encoding.UTF8.GetString(content.GetBuffer(), 0, (int)content.Length), sourceUrl, null);
        if (!parsed.Recognized || parsed.Declarations.Count == 0)
            throw new InvalidDataException("Файл не містить очікуваної публічної картки зі списком декларацій. Збережіть відкриту картку людини, а не сторінку перевірки доступу чи окрему декларацію.");
        return parsed;
    }
}
