using MediaParser.Models;
using Microsoft.AspNetCore.Mvc;
using System.Net.Http;
using System.Threading.Tasks;
using System.Collections.Generic;
using MediaParser.Services.DBServices;
using MediaParser.Services;
using MediaParser.Models.DBModels;
using MediaParser.Classes;
using MediaParser.Repositories;
using Newtonsoft.Json;

namespace MediaParser.Controllers
{
    public class ParserController : Controller
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly MediaService _mediaService;
        private readonly ResultsRepository _resultsRepository;

        public ParserController(IHttpClientFactory httpClientFactory, MediaService mediaService, ResultsRepository resultsRepository)
        {
            _mediaService = mediaService;
            _httpClientFactory = httpClientFactory;
            _resultsRepository = resultsRepository;
        }

        public IActionResult MyResults()
        {
            var results = _resultsRepository.GetListResults();
            return View(results);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult ExportExcel(bool isExportLastSearch)
        {
            List<ResultsModel> results = new();

            if (isExportLastSearch) results = _resultsRepository.GetListLustResults();
            else results = _resultsRepository.GetListResults();

            var fileBytes = ExcelExport.GenerateExcelFile(results);

            // Отправляем файл пользователю
            return File(fileBytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "results.xlsx");
        }

        [HttpGet]
        public IActionResult SearchByMedia() => View();

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SearchByMedia(SearchIdentifiers searchIdentifiers)
        {
            if (!ModelState.IsValid)
            {
                return View(searchIdentifiers);
            }
            if (searchIdentifiers.Nicknames == null || searchIdentifiers.Nicknames.Count == 0)
            {
                ModelState.AddModelError("", "Введіть хоча б одне поле або додайте його, якщо його немає.");
                return View();
            }
            await _mediaService.UpdateMedia();
            var selectedMedia = _mediaService.GetSelectedMedia();
            if (selectedMedia.Count == 0)
            {
                ModelState.AddModelError("", "❌ Не вибрано соцмережі для пошуку.");
                return View(searchIdentifiers);
            }
            foreach (var nickname in searchIdentifiers.Nicknames)
            {
                if (string.IsNullOrWhiteSpace(nickname))
                {
                    ModelState.AddModelError("", "Заповніть усі поля або видаліть зайві");
                    return View(searchIdentifiers);
                }
            }
            searchIdentifiers.Nicknames = searchIdentifiers.Nicknames.Select(value => value.Trim()).ToList();
            searchIdentifiers.Profile = string.IsNullOrWhiteSpace(searchIdentifiers.Profile) ? "Default" : searchIdentifiers.Profile.Trim();

            List<ResultsModel> results = new List<ResultsModel>();

            foreach (var nickname in searchIdentifiers.Nicknames)
            {
                var accountResult = await SearchAccounts(nickname, searchIdentifiers.Profile);
                accountResult.Date = DateTime.Now;
                results.Add(accountResult);
            }

            _resultsRepository.AddResults(results);
            ViewData["Results"] = results;

            return View(searchIdentifiers);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SelectMedia(List<string>? selectedMedia)
        {
            await _mediaService.UpdateMedia();
            _mediaService.SetSelectedMedia(selectedMedia);
            return Json(new { success = true });
        }

        private async Task<ResultsModel> SearchAccounts(string nickname, string profile)
        {
            var results = new ResultsModel { Nickname = nickname, Results = new Dictionary<string, (string Status, string ProfileUrl)>() };
            var selectedMedia = _mediaService.GetSelectedMedia();

            if (selectedMedia.Count == 0)
            {
                results.Results["Помилка"] = ("❌ Не вибрано соцмережі для пошуку.", "");
                return results;
            }

            // Separate API and Web media
            var apiMedia = selectedMedia.Where(m => !string.IsNullOrWhiteSpace(m.ApiUrl)).ToList();
            var webMedia = selectedMedia.Where(m => string.IsNullOrWhiteSpace(m.ApiUrl) && !string.IsNullOrWhiteSpace(m.SearchUrl)).ToList();
            foreach (var media in selectedMedia.Except(apiMedia).Except(webMedia))
                results.Results[media.Name] = ("❌ Для джерела не задано URL пошуку або API.", "");

            // Process API requests
            foreach (var media in apiMedia)
            {
                try
                {
                    string url = SearchAlgorithm.BuildSearchUrl(media.ApiUrl!, nickname);
                    string status = await SearchAlgorithm.CheckAccountAsync(url, media.ApiKey, media, profile);
                    results.Results[media.Name] = (status, "");
                }
                catch (Exception ex)
                {
                    results.Results[media.Name] = ($"❌ Помилка: {ex.Message}", "");
                }
            }

            // Process Web requests with a single driver
            if (webMedia.Any())
            {
                // Validate each URL before starting Chrome, so malformed sources remain visible as individual errors.
                var validWebMedia = new List<(MediaSource Media, string Url)>();
                foreach (var media in webMedia)
                {
                    try
                    {
                        validWebMedia.Add((media, SearchAlgorithm.BuildSearchUrl(media.SearchUrl!, nickname)));
                    }
                    catch (Exception ex)
                    {
                        results.Results[media.Name] = ($"❌ Помилка: {ex.Message}", "");
                    }
                }
                if (validWebMedia.Count > 0)
                {
                    try
                    {
                        using var driver = SearchAlgorithm.CreateChromeDriver(profile);
                        foreach (var (media, url) in validWebMedia)
                        {
                            string status = await SearchAlgorithm.CheckAccountAsync(url, null, media, profile, driver);
                            results.Results[media.Name] = (status, url);
                        }
                    }
                    catch (Exception ex)
                    {
                        foreach (var (media, url) in validWebMedia.Where(item => !results.Results.ContainsKey(item.Media.Name)))
                            results.Results[media.Name] = ($"❌ Не вдалося запустити браузер: {ex.Message}", url);
                    }
                }
            }

            return results;
        }

    }
}
