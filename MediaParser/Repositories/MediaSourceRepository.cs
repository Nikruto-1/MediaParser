using MediaParser.Data;
using MediaParser.IRepositories;
using MediaParser.Models.DBModels;
using Microsoft.EntityFrameworkCore;

namespace MediaParser.Repositories
{
    public class MediaSourceRepository : IMediaSourceRepository
    {
        private readonly AppDBContext _context;

        public MediaSourceRepository(AppDBContext context)
        {
            _context = context;
        }

        public async Task AddMediaAsync(MediaSource mediaSource)
        {
            await _context.MediaSources.AddAsync(mediaSource);
        }
        public async Task<List<MediaSource>> GetMediaListAsync()
        {
            return await _context.MediaSources
                .OrderBy(media => media.Id)
                .ToListAsync();
        }

        public async Task<MediaSource?> GetMediaAsync(int id)
        {
            return await _context.MediaSources
                .FirstOrDefaultAsync(t => t.Id == id);
        }

        public async Task DeleteMediaAsync(int id)
        {
            var media = await _context.MediaSources.FindAsync(id);
            if (media != null) _context.MediaSources.Remove(media);
        }

        public void UpdateMediaAsync(MediaSource mediaSource)
        {
            _context.MediaSources.Update(mediaSource);
        }
    }
}
