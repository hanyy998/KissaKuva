using System.Globalization;
using KissaKuva;

Options options;
try
{
    if (args.Any(a => a is "-h" or "--help" or "/?"))
    {
        Options.PrintUsage();
        return 0;
    }
    options = Options.Parse(args);
}
catch (ArgumentException ex)
{
    Console.Error.WriteLine($"Error: {ex.Message}");
    Console.Error.WriteLine("See: KissaKuva --help");
    return 2;
}

var state = StateStore.Load(options.StatePath);
using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
http.DefaultRequestHeaders.UserAgent.ParseAdd("KissaKuva/1.0 (cat picture downloader)");

var reddit = new RedditClient(http);
var videos = new VideoDownloader(http, options.FfmpegPath);

Console.WriteLine($"Config file: {Path.GetFullPath(options.ConfigPath)}");
Console.WriteLine(reddit.UsesOAuth
    ? "Reddit access: OAuth (REDDIT_CLIENT_ID)"
    : "Reddit access: public RSS feed (slow; set REDDIT_CLIENT_ID/SECRET for faster fetching)");

if (options.DownloadVideos && videos.FfmpegPath is null)
{
    // Without ffmpeg only a video's preview image can be checked; filtering needs a reliable check.
    if (options.RejectNonCats)
    {
        Console.Error.WriteLine("Error: rejectNonCats needs ffmpeg to check videos, and it was not found.");
        Console.Error.WriteLine("Install it (winget install Gyan.FFmpeg), set \"ffmpegPath\" or use --no-videos.");
        return 2;
    }
    Console.WriteLine("Note: ffmpeg not found; Reddit videos are saved without audio " +
                      "and checked from their preview image only.");
}

CatClassifier classifier;
try
{
    classifier = await CatClassifier.CreateAsync(options.YoloModel, options.YoloClassModel,
        options.Confidence, options.ClassConfidence, http);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Error: loading the YOLO model failed: {ex.Message}");
    return 2;
}
using var classifierScope = classifier;
Console.WriteLine(
    $"Cat detection: {Path.GetFileName(options.YoloModel)} (threshold {Num(options.Confidence)})" +
    (classifier.UsesClassification
        ? $" + {Path.GetFileName(options.YoloClassModel)} (threshold {Num(options.ClassConfidence)})"
        : "") +
    (options.RejectNonCats ? "; posts without a cat are rejected" : "; everything is saved"));

// Files go to <outputDir>/<download day>/<subreddit>/; one day per run, even across midnight.
var day = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
int totalImages = 0, totalVideos = 0, totalRejected = 0, failures = 0;

foreach (var subreddit in options.Subreddits)
{
    var subState = state.Get(subreddit);
    Console.WriteLine();
    Console.WriteLine(subState.LastPostUtc is { } last
        ? $"== r/{subreddit}: fetching posts after {DateTimeOffset.FromUnixTimeSeconds((long)last).ToLocalTime():yyyy-MM-dd HH:mm}"
        : $"== r/{subreddit}: first fetch, up to {options.Limit} newest posts");

    List<RedditPost> posts;
    try
    {
        posts = await reddit.GetNewPostsAsync(subreddit, subState.LastPostUtc, options.Limit);
    }
    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
                                   or System.Text.Json.JsonException or System.Xml.XmlException)
    {
        Console.Error.WriteLine($"  Fetch failed: {ex.Message}");
        failures++;
        continue;
    }

    Console.WriteLine($"  New image and video posts: {posts.Count}");
    var targetDir = Path.Combine(options.OutputDir, day, subreddit);
    var rejectedDir = Path.Combine(options.OutputDir, "_rejected", day, subreddit);

    // Oldest first, so the state can be saved after each post.
    foreach (var post in posts.OrderBy(p => p.CreatedUtc))
    {
        var workDir = Path.Combine(Path.GetTempPath(), "kissakuva", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        try
        {
            await ProcessPostAsync(post, subreddit, workDir, targetDir, rejectedDir);
        }
        finally
        {
            try { Directory.Delete(workDir, recursive: true); } catch (IOException) { }
        }

        subState.LastPostUtc = Math.Max(subState.LastPostUtc ?? 0, post.CreatedUtc);
        StateStore.Save(options.StatePath, state);
    }

    subState.LastRun = DateTimeOffset.UtcNow;
    StateStore.Save(options.StatePath, state);
}

Console.WriteLine();
Console.WriteLine($"Done. Saved {totalImages} images and {totalVideos} videos" +
    (options.RejectNonCats ? $", rejected {totalRejected}" : "") +
    (failures > 0 ? $", errors {failures}" : "") + ".");
return failures > 0 ? 1 : 0;

// ---------------------------------------------------------------- processing

/// <summary>
/// Downloads and checks every file of a post, decides for the whole post (one cat is enough)
/// and prints one line for the post.
/// </summary>
async Task ProcessPostAsync(RedditPost post, string subreddit, string workDir, string targetDir, string rejectedDir)
{
    var kind = post.Media.Count > 1 ? $"gallery ({post.Media.Count})"
        : post.Media[0].IsVideo ? "video" : "image";
    var files = new List<PostFile>();
    int existing = 0, videosOff = 0, unknown = 0, errors = 0;

    for (int i = 0; i < post.Media.Count; i++)
    {
        var item = post.Media[i];
        if (item.IsVideo && !options.DownloadVideos) { videosOff++; continue; }

        var suffix = post.Media.Count > 1 ? $"_{i + 1}" : "";
        var baseName = $"{DateTimeOffset.FromUnixTimeSeconds((long)post.CreatedUtc):yyyyMMdd_HHmmss}_{post.Id}{suffix}";
        if (AlreadySaved(options.OutputDir, subreddit, baseName)) { existing++; continue; }

        try
        {
            var file = item.IsVideo
                ? await DownloadVideoAsync(item, post, workDir, i, baseName)
                : await DownloadImageAsync(item, workDir, i, baseName);
            if (file is null) unknown++;
            else files.Add(file);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException
                                       or InvalidOperationException or System.Xml.XmlException)
        {
            Console.Error.WriteLine($"  [error] {item.Url}: {ex.Message}");
            errors++;
            failures++;
        }
    }

    if (files.Count == 0)
    {
        var reason = errors > 0 ? "error"
            : existing > 0 ? "skipped (exists)"
            : videosOff > 0 ? "skipped (videos off)"
            : unknown > 0 ? "skipped (unknown format)"
            : "skipped";
        Report(kind, "–", reason, post.Title);
        return;
    }

    var cats = files.Where(f => f.Result.Verdict == CatVerdict.Cat).ToList();
    var summary = post.Media.Count == 1
        ? files[0].Result.Summary
        : cats.Count > 0
            ? $"cat {cats.Count}/{files.Count}, best {cats.MaxBy(f => f.Result.Score)!.Result.Summary}"
            : $"no cat 0/{files.Count}";

    string action;
    if (options.RejectNonCats && cats.Count == 0)
    {
        if (options.KeepRejected)
        {
            Directory.CreateDirectory(rejectedDir);
            foreach (var f in files) File.Move(f.TempPath, Path.Combine(rejectedDir, f.BaseName + f.Extension), overwrite: true);
            action = "rejected → _rejected";
        }
        else
        {
            action = "rejected";
        }
        totalRejected += files.Count;
    }
    else
    {
        Directory.CreateDirectory(targetDir);
        foreach (var f in files)
        {
            File.Move(f.TempPath, Path.Combine(targetDir, f.BaseName + f.Extension), overwrite: true);
            if (f.IsVideo) totalVideos++; else totalImages++;
        }
        action = "saved" + (files.Any(f => f.IsVideo && !f.WithAudio) ? ", no audio" : "");
    }
    Report(kind, summary, action, post.Title);
}

async Task<PostFile?> DownloadImageAsync(MediaItem item, string workDir, int index, string baseName)
{
    var bytes = await http.GetByteArrayAsync(item.Url);
    var mediaType = ImageFormat.Detect(bytes);
    if (mediaType is null) return null;

    var ext = ImageFormat.Extension(mediaType);
    var path = Path.Combine(workDir, $"image{index}{ext}");
    await File.WriteAllBytesAsync(path, bytes);
    return new PostFile(path, ext, baseName, false, true, Classify(bytes));
}

async Task<PostFile> DownloadVideoAsync(MediaItem item, RedditPost post, string workDir, int index, string baseName)
{
    // The preview image shows the post, so it only fits a post with a single video.
    CatResult? result = null;
    if (post.Media.Count == 1 && post.ThumbnailUrl is not null)
    {
        try
        {
            result = Classify(await http.GetByteArrayAsync(post.ThumbnailUrl));
        }
        catch (HttpRequestException) { /* fall back to the video's frames */ }
    }

    var path = Path.Combine(workDir, $"video{index}.mp4");
    var withAudio = await videos.DownloadAsync(item, path, workDir);

    // No cat in the preview image: go through the video's frames until a cat is found.
    if (result?.Verdict != CatVerdict.Cat)
    {
        await foreach (var frame in videos.ExtractFramesAsync(path, workDir))
        {
            var frameResult = Classify(frame);
            if (frameResult.Verdict == CatVerdict.Cat || result is null or { Verdict: CatVerdict.Unverifiable })
                result = frameResult;
            if (frameResult.Verdict == CatVerdict.Cat) break;
        }
    }

    return new PostFile(path, ".mp4", baseName, true, withAudio, result ?? CatResult.Unverifiable);
}

CatResult Classify(byte[] image)
{
    try
    {
        return classifier.Classify(image);
    }
    catch (Exception ex)
    {
        return new CatResult(CatVerdict.Unverifiable, 0, $"detection failed ({ex.GetType().Name})");
    }
}

static void Report(string kind, string result, string action, string title)
{
    var shortTitle = title.Length > 50 ? title[..47] + "..." : title;
    Console.WriteLine($"  {kind,-13} {result,-44} {action,-21} {shortTitle}");
}

static string Num(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

/// <summary>True if the file was already saved on any day (<c>&lt;outputDir&gt;/&lt;day&gt;/&lt;subreddit&gt;/</c>).</summary>
static bool AlreadySaved(string outputDir, string subreddit, string baseName) =>
    Directory.Exists(outputDir) && Directory.EnumerateDirectories(outputDir)
        .Select(dayDir => Path.Combine(dayDir, subreddit))
        .Any(dir => Directory.Exists(dir) && Directory.EnumerateFiles(dir, baseName + ".*").Any());

/// <summary>A downloaded and checked file waiting for the post's decision.</summary>
sealed record PostFile(string TempPath, string Extension, string BaseName, bool IsVideo, bool WithAudio, CatResult Result);
