using MediaParser.Models.DBModels;
using MediaParser.Services.DBServices;
using Newtonsoft.Json;

namespace MediaParser.Services
{
    public class MediaService
    {
        private const string SessionKey = "UserMediaSources";
        private readonly MediaSourceService _mediaSourceService;
        private readonly IHttpContextAccessor _httpContextAccessor;

        public MediaService(MediaSourceService mediaSourceService, IHttpContextAccessor httpContextAccessor)
        {
            _mediaSourceService = mediaSourceService;
            _httpContextAccessor = httpContextAccessor;
        }

        private ISession Session => _httpContextAccessor.HttpContext?.Session
            ?? throw new InvalidOperationException("Media selections require an active HTTP session.");

        // Keep current database records and preserve each user's choices by immutable ID.
        public Task UpdateMediaLoyout() => UpdateMedia();

        public async Task UpdateMedia()
        {
            var selectedIds = GetMediaSources().Where(source => source.IsSelected)
                .Select(source => source.Media.Id).ToHashSet();
            var listMedia = await _mediaSourceService.GetMediaListAsync();
            Save(listMedia.Select(media => (media, selectedIds.Contains(media.Id))).ToList());
        }

        public List<(MediaSource Media, bool IsSelected)> GetMediaSources()
        {
            var storedData = Session.GetString(SessionKey);
            if (string.IsNullOrEmpty(storedData)) return new();
            try
            {
                return JsonConvert.DeserializeObject<List<(MediaSource Media, bool IsSelected)>>(storedData)?
                    .Where(source => source.Media != null).ToList() ?? new();
            }
            catch (JsonException)
            {
                Session.Remove(SessionKey);
                return new();
            }
        }

        public void SetSelectedMedia(List<string>? selectedMedia)
        {
            var selectedNames = new HashSet<string>(selectedMedia ?? new(), StringComparer.Ordinal);
            Save(GetMediaSources().Select(source => (source.Media, selectedNames.Contains(source.Media.Name))).ToList());
        }

        public List<MediaSource> GetSelectedMedia() => GetMediaSources()
            .Where(source => source.IsSelected).Select(source => source.Media).ToList();

        private void Save(List<(MediaSource Media, bool IsSelected)> sources) =>
            Session.SetString(SessionKey, JsonConvert.SerializeObject(sources));
    }
}
