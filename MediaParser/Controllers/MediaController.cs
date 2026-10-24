using MediaParser.Models.DBModels;
using MediaParser.Services;
using MediaParser.Services.DBServices;
using Microsoft.AspNetCore.Mvc;

namespace MediaParser.Controllers
{
    public class MediaController : Controller
    {
        private const string EditableFields = "Name,ApiUrl,SearchUrl,ApiKey,ErrorKeywordsRaw,LinkType";
        private readonly MediaService _mediaService;
        private readonly MediaSourceService _mediaSourceService;

        public MediaController(MediaService mediaService, MediaSourceService mediaSourceService)
        {
            _mediaService = mediaService;
            _mediaSourceService = mediaSourceService;
        }

        public async Task<IActionResult> EditMedia()
        {
            await _mediaService.UpdateMedia();
            return View(_mediaService.GetMediaSources().Select(source => source.Media).ToList());
        }

        [HttpGet]
        public IActionResult Create() => View(new MediaSource());

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create([Bind(EditableFields)] MediaSource mediaSource)
        {
            if (!ModelState.IsValid) return View(mediaSource);
            Normalize(mediaSource);
            if (!await _mediaSourceService.AddMediaAsync(mediaSource))
            {
                ModelState.AddModelError("", "Не вдалося додати медіа до бази даних.");
                return View(mediaSource);
            }
            await _mediaService.UpdateMedia();
            return RedirectToAction(nameof(EditMedia));
        }

        [HttpGet]
        public async Task<IActionResult> Update(int id)
        {
            var media = await _mediaSourceService.GetMediaAsync(id);
            if (media == null) return NotFound();
            // Never render stored API credentials in a form.
            return View(new MediaSource
            {
                Id = media.Id, Name = media.Name, SearchUrl = media.SearchUrl,
                ApiUrl = media.ApiUrl, ErrorKeywordsRaw = media.ErrorKeywordsRaw, LinkType = media.LinkType
            });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Update([Bind("Id," + EditableFields)] MediaSource mediaSource)
        {
            if (mediaSource.Id <= 0 || await _mediaSourceService.GetMediaAsync(mediaSource.Id) == null)
                return NotFound();
            if (!ModelState.IsValid) return View(mediaSource);
            Normalize(mediaSource);
            if (!await _mediaSourceService.UpdateMediaAsync(mediaSource))
            {
                ModelState.AddModelError("", "Не вдалося оновити медіа в базі даних.");
                return View(mediaSource);
            }
            await _mediaService.UpdateMedia();
            return RedirectToAction(nameof(EditMedia));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Delete(int id)
        {
            if (!await _mediaSourceService.DeleteMediaAsync(id))
                return Json(new { success = false, errorMessage = "Медіа не знайдено або не вдалося видалити." });
            await _mediaService.UpdateMedia();
            return Json(new { success = true });
        }

        private static void Normalize(MediaSource media)
        {
            media.Name = media.Name.Trim();
            media.SearchUrl = string.IsNullOrWhiteSpace(media.SearchUrl) ? null : media.SearchUrl.Trim();
            media.ApiUrl = string.IsNullOrWhiteSpace(media.ApiUrl) ? null : media.ApiUrl.Trim();
        }
    }
}
