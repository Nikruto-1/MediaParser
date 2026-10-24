using MediaParser.IRepositories;
using MediaParser.Models.DBModels;

namespace MediaParser.Services.DBServices
{
    public class MediaSourceService
    {
        private readonly IUnitOfWork _unitOfWork;
        private readonly ILogger<MediaSourceService> _logger;

        public MediaSourceService(IUnitOfWork unitOfWork, ILogger<MediaSourceService> logger)
        {
            _unitOfWork = unitOfWork;
            _logger = logger;
        }
        public async Task<bool> AddMediaAsync(MediaSource? mediaSource)
        {
            if (mediaSource == null) return false;
            try
            {
                await _unitOfWork.MediaSourceRepository.AddMediaAsync(mediaSource);
                await _unitOfWork.CommitAsync();
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "error when adding Media to table");
                return false;
            }
        }

        public async Task<bool> UpdateMediaAsync(MediaSource mediaSource)
        {
            try
            {
                var existing = await _unitOfWork.MediaSourceRepository.GetMediaAsync(mediaSource.Id);
                if (existing == null) return false;
                existing.Name = mediaSource.Name.Trim();
                existing.SearchUrl = mediaSource.SearchUrl?.Trim();
                existing.ApiUrl = mediaSource.ApiUrl?.Trim();
                // The edit form never sends an existing secret back to the browser.
                if (!string.IsNullOrWhiteSpace(mediaSource.ApiKey)) existing.ApiKey = mediaSource.ApiKey;
                existing.ErrorKeywordsRaw = mediaSource.ErrorKeywordsRaw;
                existing.LinkType = mediaSource.LinkType;
                _unitOfWork.MediaSourceRepository.UpdateMediaAsync(existing);
                await _unitOfWork.CommitAsync();
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error when updating media source {Id}", mediaSource.Id);
                return false;
            }
        }
        public async Task<List<MediaSource>> GetMediaListAsync()
        {
            return await _unitOfWork.MediaSourceRepository.GetMediaListAsync();
        }
        public async Task<MediaSource?> GetMediaAsync(int id)
        {
            return await _unitOfWork.MediaSourceRepository.GetMediaAsync(id);
        }
        public async Task<bool> DeleteMediaAsync(int id)
        {
            try
            {
                if (await _unitOfWork.MediaSourceRepository.GetMediaAsync(id) == null) return false;
                await _unitOfWork.MediaSourceRepository.DeleteMediaAsync(id);
                await _unitOfWork.CommitAsync();
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error when accessing MediaSource for id {id}", id);
                return false;
            }
        }
    }
}
