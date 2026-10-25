using MediaParser.Models;
using Newtonsoft.Json;

namespace MediaParser.Repositories
{
    public class ResultsRepository
    {
        private readonly IHttpContextAccessor _httpContextAccessor;

        public ResultsRepository(IHttpContextAccessor httpContextAccessor)
        {
            _httpContextAccessor = httpContextAccessor;
        }

        private ISession Session => _httpContextAccessor.HttpContext?.Session
            ?? throw new InvalidOperationException("Search results require an active HTTP session.");

        public List<ResultsModel> GetListResults() => ReadResults("UserResults");

        public List<ResultsModel> GetListLustResults() => ReadResults("LastResults");

        public void AddResult(ResultsModel result) => AddResults(new List<ResultsModel> { result });

        public void AddResults(List<ResultsModel> results)
        {
            ArgumentNullException.ThrowIfNull(results);
            var allResults = GetListResults();
            allResults.AddRange(results);
            Session.SetString("UserResults", JsonConvert.SerializeObject(allResults));
            Session.SetString("LastResults", JsonConvert.SerializeObject(results));
        }

        public void ClearResults()
        {
            Session.Remove("UserResults");
            Session.Remove("LastResults");
        }

        public void SaveJob(SearchJobSnapshot job)
        {
            if (job.State is not ("completed" or "cancelled" or "failed")) return;
            if (GetListResults().Any(result => result.JobId == job.Id)) return;
            AddResults(ToResults(job));
        }

        public static List<ResultsModel> ToResults(SearchJobSnapshot job) => job.Results
            .GroupBy(result => result.Identifier).Select(group => new ResultsModel
            {
                Nickname = group.Key,
                JobId = job.Id,
                Date = job.StartedAt.UtcDateTime,
                Details = group.ToList(),
                Results = group.GroupBy(result => result.Site).ToDictionary(site => site.Key, site =>
                {
                    var row = site.Last();
                    return ($"{row.Label}: {row.Reason}", row.ProfileUrl);
                })
            }).ToList();

        private List<ResultsModel> ReadResults(string key)
        {
            var data = Session.GetString(key);
            if (string.IsNullOrEmpty(data)) return new();
            try
            {
                var results = JsonConvert.DeserializeObject<List<ResultsModel>>(data) ?? new();
                results.RemoveAll(result => result == null);
                foreach (var result in results)
                {
                    result.Nickname ??= string.Empty;
                    result.Details ??= new();
                    result.Results = (result.Results ?? new()).ToDictionary(pair => pair.Key,
                        pair => (pair.Value.Status ?? string.Empty, pair.Value.ProfileUrl ?? string.Empty));
                }
                return results;
            }
            catch (JsonException)
            {
                Session.Remove(key);
                return new();
            }
        }
    }
}
