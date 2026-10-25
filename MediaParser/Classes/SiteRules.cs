using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using HtmlAgilityPack;
using MediaParser.Models;
using MediaParser.Models.DBModels;

namespace MediaParser.Classes;

/// <summary>Conservative, offline rules. A requested URL or generic profile vocabulary is never positive evidence.</summary>
public static class SiteRules
{
    private static readonly string[] MissingPhrases =
    {
        "user not found", "account not found", "profile not found", "page not found", "404 not found",
        "this account doesn't exist", "this account does not exist", "this user doesn't exist",
        "this channel does not exist", "channel not found", "this page doesn't exist",
        "sorry, this page isn't available", "this page isn't available", "this profile is not available",
        "couldn't find this account", "the specified profile could not be found", "no results found",
        "сторінку не знайдено", "користувача не знайдено", "обліковий запис не знайдено",
        "страница не найдена", "пользователь не найден", "page introuvable", "seite nicht gefunden"
    };
    private static readonly string[] GatePhrases =
    {
        "please solve the captcha", "verify you are human", "verify you are not a robot", "human verification",
        "log in to continue", "sign in to continue", "login required", "authentication required", "access denied",
        "too many requests", "rate limit exceeded", "увійдіть, щоб продовжити", "войдите, чтобы продолжить",
        "підтвердіть, що ви людина", "подтвердите, что вы человек", "доступ заборонено"
    };

    public static AccountCheckResult EvaluateHtml(string html, string originalUrl, string finalUrl, MediaSource media)
    {
        if (!Uri.TryCreate(originalUrl, UriKind.Absolute, out var original) || !Uri.TryCreate(finalUrl, UriKind.Absolute, out var final))
            return Unknown("Не вдалося визначити адресу завантаженої сторінки.");
        var document = new HtmlDocument();
        document.LoadHtml(html);
        var visible = new HtmlDocument();
        visible.LoadHtml(html);
        foreach (var node in Nodes(visible, "//script|//style|//noscript|//*[@hidden]|//*[@aria-hidden='true']|//*[contains(translate(@style,' ',''),'display:none')]").ToList())
            node.Remove();
        string text = Clean(visible.DocumentNode.InnerText);
        var headings = Nodes(visible, "//title|//h1|//h2|//h3|//*[@role='alert']|//*[contains(@class,'error_ctn')]")
            .Select(node => Clean(node.InnerText)).Where(value => value.Length > 0).ToArray();

        if (IsGatePath(final) || headings.Any(IsLoginHeading) || GatePhrases.Any(phrase => text.Contains(phrase, StringComparison.OrdinalIgnoreCase)) ||
            Nodes(visible, "//form//input[translate(@type,'PASSWORD','password')='password']").Any() ||
            Nodes(visible, "//iframe[contains(@src,'recaptcha') or contains(@src,'hcaptcha')] | //*[@id='challenge-form']").Any())
            return new(AccountStatus.Blocked, "Джерело показало авторизацію, перевірку людини або обмеження доступу.", "Потрібна дія користувача на сайті; перевірку не обходимо.");

        if (IsUnrelatedRedirect(original, final, media))
            return Unknown("Джерело перенаправило запит на іншу сторінку.", "Кінцева адреса не відповідає запитаному профілю.");
        if (media.LinkType == "FullName")
            return Unknown("Відкрито список результатів пошуку за ПІБ; окремий акаунт потрібно перевірити вручну.");
        if (Site(original) == "epic" && original.AbsolutePath.StartsWith("/id/", StringComparison.OrdinalIgnoreCase))
            return Unknown("Ця адреса Epic Games не є підтвердженим способом пошуку публічного профілю за display name.",
                "Простір /id/ використовується для входу; перевірте акаунт через Epic Games або офіційний авторизований API.");

        string? site = Site(original);
        string? identity = ProfileIdentity(original, site);
        AccountCheckResult? positive = site != null && identity != null
            ? PositiveEvidence(document, original, site, identity) : null;
        string? missing = headings.FirstOrDefault(heading => MissingPhrases.Any(phrase => heading.Contains(phrase, StringComparison.OrdinalIgnoreCase)) ||
            Regex.IsMatch(heading, @"^404(?:\s|$|[—:–-])", RegexOptions.CultureInvariant));
        if (missing != null)
            return positive != null ? Unknown("Сторінка містить суперечливі ознаки профілю та помилки.") :
                new(AccountStatus.NotFound, "Сторінка явно повідомляє, що профіль або ресурс не знайдено.", "Маркер відсутності в заголовку або блоці помилки.");
        if (positive != null) return positive;

        // Arbitrary database keywords can describe a temporary failure or even a valid private profile.
        // They are diagnostics, never an unconditional absent-account verdict.
        if (media.ErrorKeywords.Any(keyword => !string.IsNullOrWhiteSpace(keyword) && headings.Any(heading =>
            heading.Equals(Clean(keyword), StringComparison.OrdinalIgnoreCase))))
            return Unknown("Виявлено налаштований маркер помилки, але відсутність акаунта не підтверджена.");
        return Unknown("Сторінка не містить достатніх доказів існування саме запитаного акаунта.",
            "Сам URL, ім’я в тексті, followers, profile або bio не є достатнім доказом.");
    }

    public static AccountCheckResult EvaluateApi(string json, string originalUrl, string finalUrl, MediaSource media)
    {
        if (!Uri.TryCreate(originalUrl, UriKind.Absolute, out var original) || !Uri.TryCreate(finalUrl, UriKind.Absolute, out var final))
            return Unknown("Не вдалося визначити адресу відповіді API.");
        if (IsGatePath(final)) return new(AccountStatus.Blocked, "API перенаправив запит на авторизацію.");
        if (IsUnrelatedRedirect(original, final, media)) return Unknown("API перенаправив запит на інший ресурс.");
        if (media.LinkType == "FullName")
            return Unknown("API повернув результати пошуку за ПІБ; окремий акаунт потрібно перевірити вручну.");
        try
        {
            using var parsed = JsonDocument.Parse(json);
            var root = parsed.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return Unknown("API повернув неочікувану структуру JSON.");
            string message = String(root, "message") ?? String(root, "description") ?? String(root, "error") ?? "";
            if (GatePhrases.Concat(new[] { "bad credentials", "invalid api key", "invalid token", "unauthorized" })
                .Any(phrase => message.Contains(phrase, StringComparison.OrdinalIgnoreCase)))
                return new(AccountStatus.Blocked, "API повідомив про обмеження доступу або частоти запитів.");
            if ((root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.False) ||
                HasValue(root, "error") || HasValue(root, "errors"))
                return Unknown("API повідомив про помилку; існування акаунта не визначено.");

            string? site = Site(original);
            string? identity = ProfileIdentity(original, site);
            if (site == "github-api" && identity != null && EqualIdentity(String(root, "login"), identity) &&
                String(root, "type") is "User" or "Organization" && PositiveId(root, "id") &&
                MatchesProfile(String(root, "html_url"), "github", identity))
                return Found("GitHub API підтвердив точний ідентифікатор акаунта.", "login збігається з запитом; id, type та html_url відповідають схемі GitHub.");
            return Unknown("API не надав перевірюваної відповіді з точним ідентифікатором запитаного акаунта.",
                "Наявність довільних полів data або id не підтверджує акаунт.");
        }
        catch (JsonException) { return Unknown("API повернув некоректний JSON."); }
    }

    private static AccountCheckResult? PositiveEvidence(HtmlDocument document, Uri original, string site, string identity)
    {
        string? profileNamespace = ProfileNamespace(original, site);
        bool exactUrl = Meta(document, "og:url").Any(url => MatchesProfile(url, site, identity, profileNamespace)) ||
            Nodes(document, "//link[@href]").Any(node => Tokens(node.GetAttributeValue("rel", "")).Contains("canonical") && MatchesProfile(node.GetAttributeValue("href", ""), site, identity, profileNamespace));
        if (HasProfileSchema(document, site, identity, profileNamespace))
            return Found("Структуровані дані профілю підтверджують запитаний ідентифікатор.", "ProfilePage/Person/Organization: точні alternateName та URL профілю.");
        if (!exactUrl && site != "youtube") return null;

        bool exactProfileMeta = Meta(document, "profile:username").Any(value => EqualIdentity(value, identity)) &&
            Meta(document, "og:type").Any(value => value.Equals("profile", StringComparison.OrdinalIgnoreCase));
        if (exactUrl && exactProfileMeta)
            return Found("Метадані сторінки підтверджують точний ідентифікатор профілю.", "profile:username та canonical/og:url збігаються з запитом; og:type=profile.");

        if (site == "github" && exactUrl && Meta(document, "octolytics-dimension-user_login").Any(value => EqualIdentity(value, identity)) &&
            Meta(document, "octolytics-dimension-user_id").Any(IsPositiveInteger))
            return Found("GitHub підтвердив профіль із запитаним логіном.", "octolytics user_login та user_id узгоджуються з canonical/og:url.");
        if (site == "instagram" && exactUrl && Meta(document, "instapp:owner_user_id").Any(IsPositiveInteger) &&
            Meta(document, "og:title").Any(title => ContainsHandle(title, identity)))
            return Found("Instagram підтвердив профіль із запитаним username.", "owner_user_id, точний @username в og:title та canonical/og:url узгоджені.");
        if (site == "instagram" && exactUrl && Meta(document, "og:type").Any(value => value == "profile") &&
            Meta(document, "og:title").Any(title => ContainsHandle(title, identity)) &&
            Meta(document, "al:ios:url").Concat(Meta(document, "al:android:url")).Any(value => IsInstagramAppLink(value, identity)))
            return Found("Instagram підтвердив точний нікнейм у публічному профілі.",
                "Адреса профілю, нікнейм у заголовку та посилання застосунку Instagram збігаються.");
        if (site == "snapchat" && exactUrl && Meta(document, "og:title").Any(title => ContainsHandle(title, identity)) &&
            Meta(document, "twitter:app:url:iphone").Any(value => Uri.TryCreate(value, UriKind.Absolute, out var deepLink) &&
                deepLink.Scheme == "snapchat" && deepLink.Host == "add" && EqualIdentity(deepLink.Segments.LastOrDefault(), identity)))
            return Found("Snapchat підтвердив публічний профіль із запитаним username.", "Точний @username, canonical/og:url та Snapchat app deep link узгоджені.");
        if (site == "telegram" && exactUrl && HasClass(document, "tgme_page_title") && HasClass(document, "tgme_page_photo_image") &&
            Nodes(document, "//*[contains(concat(' ',normalize-space(@class),' '),' tgme_page_extra ')]").Any(node => EqualIdentity(Clean(node.InnerText), identity)))
            return Found("Telegram підтвердив публічний профіль із запитаним username.", "Точний @username у tgme_page_extra, назва, фото та canonical/og:url узгоджені.");
        if (site == "steam" && exactUrl && HasClass(document, "actual_persona_name") && HasClass(document, "playerAvatar") && HasClass(document, "profile_header"))
            return Found("Steam підтвердив сторінку профілю за запитаною адресою.", "Точний canonical/og:url та специфічні persona/avatar/profile_header елементи Steam.");
        if (site == "youtube")
        {
            foreach (var script in Nodes(document, "//script"))
            {
                string source = script.InnerText;
                int marker = source.IndexOf("ytInitialData", StringComparison.Ordinal);
                if (marker < 0) continue;
                int start = source.IndexOf('{', marker);
                if (start < 0) continue;
                try
                {
                    var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(source[start..]));
                    using var json = JsonDocument.ParseValue(ref reader);
                    if (json.RootElement.TryGetProperty("metadata", out var metadata) && metadata.ValueKind == JsonValueKind.Object &&
                        metadata.TryGetProperty("channelMetadataRenderer", out var channel) && channel.ValueKind == JsonValueKind.Object)
                    {
                        string? channelId = String(channel, "externalId");
                        if (MatchesProfile(String(channel, "vanityChannelUrl"), site, identity) && channelId?.StartsWith("UC", StringComparison.Ordinal) == true &&
                            channelId.Length >= 10 && Meta(document, "og:url").Any(value => MatchesYouTubeChannel(value, channelId)))
                            return Found("YouTube підтвердив канал із запитаним handle.", "vanityChannelUrl точно збігається з handle; externalId узгоджується з og:url каналу.");
                    }
                }
                catch (JsonException) { }
            }
        }
        return null;
    }

    private static bool HasProfileSchema(HtmlDocument document, string site, string identity, string? profileNamespace)
    {
        foreach (var script in Nodes(document, "//script[@type='application/ld+json']"))
        {
            try
            {
                using var json = JsonDocument.Parse(script.InnerText);
                foreach (var item in SchemaItems(json.RootElement))
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    var entity = item;
                    if (HasType(item, "ProfilePage"))
                    {
                        if (!item.TryGetProperty("mainEntity", out entity) || entity.ValueKind != JsonValueKind.Object) continue;
                    }
                    if (!(HasType(entity, "Person") || HasType(entity, "Organization"))) continue;
                    if (EqualIdentity(String(entity, "alternateName"), identity) &&
                        (MatchesProfile(String(entity, "url"), site, identity, profileNamespace) || MatchesProfile(String(item, "url"), site, identity, profileNamespace)))
                        return true;
                }
            }
            catch (JsonException) { }
        }
        return false;
    }

    private static IEnumerable<JsonElement> SchemaItems(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
        { foreach (var item in root.EnumerateArray()) yield return item; }
        else if (root.ValueKind == JsonValueKind.Object)
        {
            yield return root;
            if (root.TryGetProperty("@graph", out var graph) && graph.ValueKind == JsonValueKind.Array)
                foreach (var item in graph.EnumerateArray()) yield return item;
        }
    }

    private static bool HasType(JsonElement item, string expected)
    {
        if (!item.TryGetProperty("@type", out var type)) return false;
        return type.ValueKind == JsonValueKind.String ? type.GetString()?.Split('/').Last() == expected :
            type.ValueKind == JsonValueKind.Array && type.EnumerateArray().Any(value => value.ValueKind == JsonValueKind.String && value.GetString()?.Split('/').Last() == expected);
    }

    private static bool IsUnrelatedRedirect(Uri original, Uri final, MediaSource media)
    {
        if (original == final) return false;
        string? site = Site(original);
        if (site == null ? !Host(original).Equals(Host(final), StringComparison.OrdinalIgnoreCase) : Site(final) != site) return true;
        if (ProfileNamespace(original, site) != ProfileNamespace(final, site)) return true;
        if (media.LinkType == "FullName") return original.AbsolutePath.TrimEnd('/') != final.AbsolutePath.TrimEnd('/') || original.Query != final.Query;
        string? identity = ProfileIdentity(original, site);
        return identity == null ? !original.AbsolutePath.TrimEnd('/').Equals(final.AbsolutePath.TrimEnd('/'), StringComparison.OrdinalIgnoreCase) :
            !EqualIdentity(ProfileIdentity(final, site), identity);
    }

    private static bool IsGatePath(Uri url) => url.Segments.Any(segment =>
        new[] { "login", "signin", "sign-in", "auth", "checkpoint", "challenge", "captcha", "consent" }.Contains(segment.Trim('/'), StringComparer.OrdinalIgnoreCase));
    private static bool IsLoginHeading(string text) => new[] { "log in", "login", "sign in", "увійти", "вхід", "войти", "авторизація" }
        .Any(value => text.Equals(value, StringComparison.OrdinalIgnoreCase) || text.StartsWith(value + " •", StringComparison.OrdinalIgnoreCase) || text.StartsWith(value + " |", StringComparison.OrdinalIgnoreCase));
    private static string Host(Uri uri) => uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? uri.Host[4..].ToLowerInvariant() : uri.Host.ToLowerInvariant();
    private static string? Site(Uri uri) => Host(uri) switch
    {
        "github.com" => "github", "api.github.com" => "github-api", "instagram.com" => "instagram", "snapchat.com" => "snapchat",
        "t.me" or "telegram.me" => "telegram", "steamcommunity.com" => "steam", "youtube.com" or "m.youtube.com" => "youtube",
        "tiktok.com" => "tiktok", "twitter.com" or "x.com" => "twitter", "reddit.com" => "reddit",
        "facebook.com" or "m.facebook.com" => "facebook", "pinterest.com" => "pinterest", "linkedin.com" => "linkedin",
        "twitch.tv" => "twitch", "soundcloud.com" => "soundcloud", "rumble.com" => "rumble", "medium.com" => "medium",
        "open.spotify.com" => "spotify", "epicgames.com" => "epic", _ => null
    };
    private static string? ProfileIdentity(Uri uri, string? site)
    {
        string[] parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.UnescapeDataString).ToArray();
        return site switch
        {
            "github" or "instagram" or "telegram" or "twitter" when parts.Length == 1 => parts[0].TrimStart('@'),
            "facebook" or "pinterest" or "twitch" or "soundcloud" when parts.Length == 1 => parts[0],
            "linkedin" when parts.Length == 2 && parts[0] == "in" => parts[1],
            "rumble" when parts.Length == 1 => parts[0],
            "rumble" when parts.Length == 2 && parts[0] is "user" or "c" => parts[1],
            "medium" when parts.Length == 1 && parts[0].StartsWith('@') => parts[0][1..],
            "spotify" when parts.Length == 2 && parts[0] == "user" => parts[1],
            "github-api" when parts.Length == 2 && parts[0] == "users" => parts[1],
            "snapchat" when parts.Length == 2 && parts[0] == "add" => parts[1].TrimStart('@'),
            "snapchat" or "youtube" or "tiktok" when parts.Length == 1 && parts[0].StartsWith('@') => parts[0][1..],
            "steam" when parts.Length == 2 && parts[0] is "id" or "profiles" => parts[1],
            "reddit" when parts.Length == 2 && parts[0] == "user" => parts[1],
            _ => null
        };
    }
    // Rumble user accounts and channels can have the same spelling but represent different entities.
    // Its root vanity URLs are channel aliases, not aliases for /user/{username}.
    private static string? ProfileNamespace(Uri uri, string? site)
    {
        if (site != "rumble") return null;
        var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 2 && parts[0] == "user" ? "user" :
            parts.Length == 1 || (parts.Length == 2 && parts[0] == "c") ? "channel" : null;
    }
    private static bool MatchesProfile(string? value, string site, string identity, string? profileNamespace = null) => Uri.TryCreate(HtmlEntity.DeEntitize(value ?? ""), UriKind.Absolute, out var url) &&
        url.Scheme is "http" or "https" && Site(url) == site && EqualIdentity(ProfileIdentity(url, site), identity) &&
        (profileNamespace == null || ProfileNamespace(url, site) == profileNamespace);
    private static bool MatchesYouTubeChannel(string value, string id) => Uri.TryCreate(value, UriKind.Absolute, out var uri) && Site(uri) == "youtube" && uri.AbsolutePath.TrimEnd('/') == "/channel/" + id;
    private static bool IsInstagramAppLink(string value, string identity)
    {
        if (!Uri.TryCreate(HtmlEntity.DeEntitize(value), UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme == "instagram" && uri.Host == "user")
        {
            var usernames = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(part => part.Split('=', 2)).Where(part => part.Length == 2 && part[0] == "username")
                .Select(part => Uri.UnescapeDataString(part[1])).ToList();
            return usernames.Count == 1 && EqualIdentity(usernames[0], identity);
        }
        if (uri.Scheme is not ("http" or "https") || Site(uri) != "instagram") return false;
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length == 2 && segments[0] == "_u" && EqualIdentity(Uri.UnescapeDataString(segments[1]), identity);
    }
    private static bool EqualIdentity(string? value, string expected) => value != null &&
        Clean(value).Trim('/').TrimStart('@').Normalize(NormalizationForm.FormC).Equals(expected.Normalize(NormalizationForm.FormC), StringComparison.OrdinalIgnoreCase);
    private static bool ContainsHandle(string value, string expected) => Regex.IsMatch(value,
        @"(?<![\p{L}\p{N}_.])@" + Regex.Escape(expected) + @"(?![\p{L}\p{N}_.])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static bool IsPositiveInteger(string value) => long.TryParse(value, out long id) && id > 0;
    private static bool PositiveId(JsonElement item, string field) => item.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long id) && id > 0;
    private static string? String(JsonElement item, string field) => item.ValueKind == JsonValueKind.Object && item.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static bool HasValue(JsonElement item, string field) => item.TryGetProperty(field, out var value) && (value.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined or JsonValueKind.False => false,
        JsonValueKind.String => !string.IsNullOrWhiteSpace(value.GetString()),
        JsonValueKind.Array => value.GetArrayLength() > 0,
        JsonValueKind.Object => value.EnumerateObject().Any(), _ => true
    });
    private static IEnumerable<HtmlNode> Nodes(HtmlDocument doc, string xpath) => doc.DocumentNode.SelectNodes(xpath)?.AsEnumerable() ?? Enumerable.Empty<HtmlNode>();
    private static IEnumerable<string> Meta(HtmlDocument doc, string key) => Nodes(doc, "//meta[@content]")
        .Where(node => node.GetAttributeValue("property", "").Equals(key, StringComparison.OrdinalIgnoreCase) || node.GetAttributeValue("name", "").Equals(key, StringComparison.OrdinalIgnoreCase))
        .Select(node => HtmlEntity.DeEntitize(node.GetAttributeValue("content", "")));
    private static bool HasClass(HtmlDocument doc, string value) => Nodes(doc, "//*[@class]").Any(node => Tokens(node.GetAttributeValue("class", "")).Contains(value));
    private static string[] Tokens(string value) => value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
    private static string Clean(string value) => Regex.Replace(HtmlEntity.DeEntitize(value), @"\s+", " ").Trim().Replace('’', '\'');
    private static AccountCheckResult Found(string reason, string evidence) => new(AccountStatus.Found, reason, evidence);
    private static AccountCheckResult Unknown(string reason, string? evidence = null) => new(AccountStatus.Unconfirmed, reason, evidence);
}
