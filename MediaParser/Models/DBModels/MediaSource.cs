using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json;
using System.ComponentModel.DataAnnotations;
using System.Globalization;

namespace MediaParser.Models.DBModels
{
    public class MediaSource : IValidatableObject
    {
        public int Id { get; set; }
        [Required(ErrorMessage = "Вкажіть назву медіа.")]
        [StringLength(100)]
        public string Name { get; set; } = string.Empty; // Назва медіа
        public string? ApiUrl { get; set; } // URL API, якщо є
        public string? SearchUrl { get; set; } // URL пошуку, якщо API немає
        public string? ApiKey { get; set; } // Чи потрібен API ключ

        [Column("ErrorKeywords")]
        public string ErrorKeywordsRaw { get; set; } = "[]"; // JSON-рядок із БД

        [NotMapped]
        public string[] ErrorKeywords
        {
            get
            {
                try
                {
                    return JsonSerializer.Deserialize<string[]>(ErrorKeywordsRaw ?? "[]")?
                        .Where(keyword => !string.IsNullOrWhiteSpace(keyword)).ToArray() ?? Array.Empty<string>();
                }
                catch (JsonException)
                {
                    // Legacy data must not prevent the media list from rendering.
                    return Array.Empty<string>();
                }
            }
            set => ErrorKeywordsRaw = JsonSerializer.Serialize(value ?? Array.Empty<string>());
        }
        [RegularExpression("^(User|FullName)$", ErrorMessage = "Оберіть тип посилання.")]
        [Required]
        public string LinkType { get; set; } = "User";

        public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
        {
            if (string.IsNullOrWhiteSpace(ApiUrl) && string.IsNullOrWhiteSpace(SearchUrl))
                yield return new ValidationResult("Вкажіть Search URL або API URL.", new[] { nameof(SearchUrl) });

            foreach (var (template, member) in new[] { (SearchUrl, nameof(SearchUrl)), (ApiUrl, nameof(ApiUrl)) })
            {
                if (!string.IsNullOrWhiteSpace(template) && !IsValidUrlTemplate(template))
                    yield return new ValidationResult("Вкажіть HTTP/HTTPS URL із {0} для ідентифікатора, без зворотних слешів.", new[] { member });
            }

            if (!IsValidErrorKeywords(ErrorKeywordsRaw))
                yield return new ValidationResult("Вкажіть JSON-масив непорожніх рядків, наприклад [\"User not found\"], або [].", new[] { nameof(ErrorKeywordsRaw) });
        }

        private static bool IsValidUrlTemplate(string template)
        {
            if (!template.Contains("{0}", StringComparison.Ordinal) || template.Contains('\\')) return false;
            try
            {
                var url = string.Format(CultureInfo.InvariantCulture, template, "test-user");
                return Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
                    (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
            }
            catch (FormatException)
            {
                return false;
            }
        }

        private static bool IsValidErrorKeywords(string? raw)
        {
            try
            {
                var keywords = JsonSerializer.Deserialize<string[]>(raw ?? "null");
                return keywords != null && keywords.All(keyword => !string.IsNullOrWhiteSpace(keyword));
            }
            catch (JsonException)
            {
                return false;
            }
        }
    }
}
