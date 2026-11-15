using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace KissaKuva;

public enum MediaKind { Image, DirectVideo, RedditVideo }

public sealed record MediaItem(MediaKind Kind, string Url)
{
    public bool IsVideo => Kind != MediaKind.Image;
}

/// <param name="ThumbnailUrl">The post's preview image (used to check videos for cats).</param>
public sealed record RedditPost(string Id, string Title, double CreatedUtc, IReadOnlyList<MediaItem> Media, string? ThumbnailUrl);

/// <summary>
/// Fetches the newest posts of subreddits.
/// If REDDIT_CLIENT_ID and REDDIT_CLIENT_SECRET are set, the official OAuth API is used
/// (oauth.reddit.com, ~100 requests/min, supports galleries). Otherwise the public RSS feed is used,
/// which works without credentials but is tightly rate-limited and only sees direct media links.
/// </summary>
public sealed partial class RedditClient
{
    private static readonly string[] ImageExtensions = [".jpg", ".jpeg", ".png", ".gif", ".webp"];
    private static readonly XNamespace Atom = "http://www.w3.org/2005/Atom";
    private static readonly XNamespace Media = "http://search.yahoo.com/mrss/";

    private readonly HttpClient _http;
    private readonly string? _clientId = Environment.GetEnvironmentVariable("REDDIT_CLIENT_ID");
    private readonly string? _clientSecret = Environment.GetEnvironmentVariable("REDDIT_CLIENT_SECRET");
    private string? _token;
    private DateTimeOffset _tokenExpires;

    public RedditClient(HttpClient http) => _http = http;

    public bool UsesOAuth => !string.IsNullOrEmpty(_clientId) && !string.IsNullOrEmpty(_clientSecret);

    private sealed record Page(List<(RedditPost? Post, double Created, bool Stickied)> Items, string? After);

    /// <summary>
    /// Returns the media posts created after <paramref name="sinceUtc"/> (or the newest ones,
    /// if there was no earlier fetch). At most <paramref name="limit"/> posts are looked at.
    /// </summary>
    public async Task<List<RedditPost>> GetNewPostsAsync(string subreddit, double? sinceUtc, int limit)
    {
        var result = new List<RedditPost>();
        string? after = null;
        int seen = 0;

        while (seen < limit)
        {
            var pageSize = Math.Min(100, limit - seen);
            var page = UsesOAuth
                ? await GetJsonPageAsync(subreddit, pageSize, after)
                : await GetRssPageAsync(subreddit, pageSize, after);
            if (page.Items.Count == 0) break;

            foreach (var (post, created, stickied) in page.Items)
            {
                // Newest first: stop at the first post that was already handled.
                // (Pinned posts can be old, so they do not stop the fetch.)
                if (sinceUtc is { } since && created <= since)
                {
                    if (stickied) continue;
                    return result;
                }

                seen++;
                if (post is not null) result.Add(post);
                if (seen >= limit) break;
            }

            after = page.After;
            if (after is null) break;
        }

        return result;
    }

    // ---------------------------------------------------------------- OAuth / JSON

    private async Task<Page> GetJsonPageAsync(string subreddit, int pageSize, string? after)
    {
        var url = $"https://oauth.reddit.com/r/{subreddit}/new?limit={pageSize}&raw_json=1"
                  + (after is null ? "" : $"&after={after}");
        var token = await GetTokenAsync();
        var body = await SendAsync(() =>
        {
            var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return req;
        });

        using var doc = JsonDocument.Parse(body);
        var data = doc.RootElement.GetProperty("data");
        var items = new List<(RedditPost?, double, bool)>();
        foreach (var child in data.GetProperty("children").EnumerateArray())
        {
            var p = child.GetProperty("data");
            var created = p.GetProperty("created_utc").GetDouble();
            bool stickied = p.TryGetProperty("stickied", out var st) && st.GetBoolean();
            bool nsfw = p.TryGetProperty("over_18", out var o) && o.GetBoolean();
            var media = nsfw ? [] : ExtractMedia(p);
            var post = media.Count == 0 ? null : new RedditPost(
                p.GetProperty("id").GetString()!, p.GetProperty("title").GetString() ?? "", created, media,
                PreviewUrl(p));
            items.Add((post, created, stickied));
        }

        var next = data.TryGetProperty("after", out var a) && a.ValueKind == JsonValueKind.String ? a.GetString() : null;
        return new Page(items, next);
    }

    private async Task<string> GetTokenAsync()
    {
        if (_token is not null && DateTimeOffset.UtcNow < _tokenExpires) return _token;

        var body = await SendAsync(() =>
        {
            var req = new HttpRequestMessage(HttpMethod.Post, "https://www.reddit.com/api/v1/access_token")
            {
                Content = new FormUrlEncodedContent([new("grant_type", "client_credentials")]),
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_clientId}:{_clientSecret}")));
            return req;
        });

        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("access_token", out var t))
            throw new HttpRequestException("Reddit OAuth authentication failed: " + body);
        _token = t.GetString();
        var expiresIn = doc.RootElement.TryGetProperty("expires_in", out var e) ? e.GetInt32() : 3600;
        _tokenExpires = DateTimeOffset.UtcNow.AddSeconds(expiresIn - 60);
        return _token!;
    }

    private static string? PreviewUrl(JsonElement p) =>
        p.TryGetProperty("preview", out var pv) && pv.TryGetProperty("images", out var imgs)
        && imgs.ValueKind == JsonValueKind.Array && imgs.GetArrayLength() > 0
        && imgs[0].TryGetProperty("source", out var src) && src.TryGetProperty("url", out var u)
            ? u.GetString()
            : null;

    private static List<MediaItem> ExtractMedia(JsonElement p)
    {
        var urls = new List<MediaItem>();

        // Gallery posts: the images are in media_metadata, their order in gallery_data.
        if (p.TryGetProperty("is_gallery", out var g) && g.ValueKind == JsonValueKind.True
            && p.TryGetProperty("media_metadata", out var meta) && meta.ValueKind == JsonValueKind.Object
            && p.TryGetProperty("gallery_data", out var gd) && gd.ValueKind == JsonValueKind.Object)
        {
            foreach (var item in gd.GetProperty("items").EnumerateArray())
            {
                var mediaId = item.GetProperty("media_id").GetString();
                if (mediaId is null || !meta.TryGetProperty(mediaId, out var m)) continue;
                if (m.TryGetProperty("status", out var status) && status.GetString() != "valid") continue;
                if (!m.TryGetProperty("s", out var s)) continue;
                if (s.TryGetProperty("u", out var u) && u.GetString() is { } img)
                    urls.Add(new MediaItem(MediaKind.Image, img));
                else if (s.TryGetProperty("mp4", out var mp4) && mp4.GetString() is { } mp4Url)
                    urls.Add(new MediaItem(MediaKind.DirectVideo, mp4Url));   // animated gallery image
                else if (s.TryGetProperty("gif", out var gif) && gif.GetString() is { } gifUrl)
                    urls.Add(new MediaItem(MediaKind.Image, gifUrl));
            }
            return urls;
        }

        // Direct link to an image or video (i.redd.it, v.redd.it, i.imgur.com, etc.).
        if ((p.TryGetProperty("url_overridden_by_dest", out var dest) || p.TryGetProperty("url", out dest))
            && dest.GetString() is { } url && ClassifyUrl(url) is { } linked)
        {
            urls.Add(linked);
        }
        return urls;
    }

    // ---------------------------------------------------------------- RSS (no credentials)

    private async Task<Page> GetRssPageAsync(string subreddit, int pageSize, string? after)
    {
        var url = $"https://www.reddit.com/r/{subreddit}/new/.rss?limit={pageSize}"
                  + (after is null ? "" : $"&after={after}");
        var body = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, url));

        var feed = XDocument.Parse(body);
        var items = new List<(RedditPost?, double, bool)>();
        string? lastId = null;
        foreach (var entry in feed.Root!.Elements(Atom + "entry"))
        {
            var fullId = entry.Element(Atom + "id")?.Value ?? "";        // e.g. t3_1xw5ydb
            var published = entry.Element(Atom + "published")?.Value ?? entry.Element(Atom + "updated")?.Value;
            if (!fullId.StartsWith("t3_") || published is null) continue;
            lastId = fullId;

            var created = DateTimeOffset.Parse(published, CultureInfo.InvariantCulture).ToUnixTimeSeconds();
            var title = entry.Element(Atom + "title")?.Value ?? "";
            var html = entry.Element(Atom + "content")?.Value ?? "";

            // The content's "[link]" points to the post's target (direct image, video, gallery...).
            var match = RssLinkRegex().Match(html);
            var link = match.Success ? WebUtility.HtmlDecode(match.Groups[1].Value) : null;
            var thumb = (string?)entry.Element(Media + "thumbnail")?.Attribute("url");
            var post = link is not null && ClassifyUrl(link) is { } item
                ? new RedditPost(fullId[3..], title, created, [item], thumb)
                : null;
            items.Add((post, created, false));
        }

        // RSS gives no next-page token; use the id of the last post.
        return new Page(items, items.Count >= pageSize ? lastId : null);
    }

    [GeneratedRegex("<a href=\"([^\"]+)\">\\[link\\]</a>")]
    private static partial Regex RssLinkRegex();

    // ---------------------------------------------------------------- shared

    /// <summary>Tells from a link whether it is an image, a direct video or a Reddit video. Anything else: null.</summary>
    private static MediaItem? ClassifyUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        if (uri.Host.Equals("v.redd.it", StringComparison.OrdinalIgnoreCase))
            return new MediaItem(MediaKind.RedditVideo, $"https://v.redd.it{uri.AbsolutePath.TrimEnd('/')}");

        var ext = Path.GetExtension(uri.AbsolutePath).ToLowerInvariant();
        if (ImageExtensions.Contains(ext)) return new MediaItem(MediaKind.Image, url);
        if (ext == ".mp4") return new MediaItem(MediaKind.DirectVideo, url);
        if (ext == ".gifv") // Imgur's .gifv is really an mp4 video
            return new MediaItem(MediaKind.DirectVideo, url[..url.LastIndexOf(".gifv", StringComparison.OrdinalIgnoreCase)] + ".mp4");
        return null;
    }

    /// <summary>Sends a request and, when needed, waits for Reddit's rate limit to reset.</summary>
    private async Task<string> SendAsync(Func<HttpRequestMessage> makeRequest)
    {
        const int maxAttempts = 6;
        for (int attempt = 1; ; attempt++)
        {
            using var req = makeRequest();
            using var resp = await _http.SendAsync(req);

            if (resp.StatusCode == HttpStatusCode.TooManyRequests && attempt < maxAttempts)
            {
                var wait = RateLimitReset(resp) ?? TimeSpan.FromSeconds(10 * attempt);
                Console.WriteLine($"  Reddit is rate-limiting requests, waiting {wait.TotalSeconds:0} s...");
                await Task.Delay(wait);
                continue;
            }

            if (resp.StatusCode == HttpStatusCode.Forbidden && !UsesOAuth)
                throw new HttpRequestException(
                    "Reddit blocked the request (403). Set REDDIT_CLIENT_ID and REDDIT_CLIENT_SECRET (see README).");
            resp.EnsureSuccessStatusCode();
            var body = await resp.Content.ReadAsStringAsync();

            // If the quota is used up, wait before the next request.
            if (resp.Headers.TryGetValues("x-ratelimit-remaining", out var rem)
                && double.TryParse(rem.First(), CultureInfo.InvariantCulture, out var remaining)
                && remaining < 1 && RateLimitReset(resp) is { } reset)
            {
                await Task.Delay(reset);
            }
            return body;
        }
    }

    private static TimeSpan? RateLimitReset(HttpResponseMessage resp)
    {
        if (resp.Headers.RetryAfter?.Delta is { } d && d > TimeSpan.Zero) return d;
        if (resp.Headers.TryGetValues("x-ratelimit-reset", out var v)
            && double.TryParse(v.First(), CultureInfo.InvariantCulture, out var secs))
            return TimeSpan.FromSeconds(Math.Clamp(secs, 1, 600) + 1);
        return null;
    }
}
