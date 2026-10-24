namespace MediaParser.Models
{
    public class ResultsModel
    {
        public Dictionary<string, (string Status, string ProfileUrl)> Results { get; set; } = new();
        public string Nickname { get; set; } = string.Empty;
        public DateTime? Date { get; set; }
    }
}
