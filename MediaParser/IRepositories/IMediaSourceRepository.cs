using MediaParser.Models.DBModels;

namespace MediaParser.IRepositories
{
    public interface IMediaSourceRepository
    {
        Task AddMediaAsync(MediaSource mediaSource);
        Task<MediaSource?> GetMediaAsync(int id);
        Task<List<MediaSource>> GetMediaListAsync();
        Task DeleteMediaAsync(int id);
        void UpdateMediaAsync(MediaSource mediaSource);
    }
}
