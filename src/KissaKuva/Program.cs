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
    Console.Error.WriteLine($"Virhe: {ex.Message}");
    Console.Error.WriteLine("Katso ohje: KissaKuva --help");
    return 2;
}

var state = StateStore.Load(options.StatePath);
using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
http.DefaultRequestHeaders.UserAgent.ParseAdd("KissaKuva/1.0 (cat picture downloader)");

var reddit = new RedditClient(http);
var videos = new VideoDownloader(http, options.FfmpegPath);

Console.WriteLine($"Parametritiedosto: {Path.GetFullPath(options.ConfigPath)}");
Console.WriteLine(reddit.UsesOAuth
    ? "Reddit-yhteys: OAuth (REDDIT_CLIENT_ID)"
    : "Reddit-yhteys: julkinen RSS-syöte (hidas; aseta REDDIT_CLIENT_ID/SECRET nopeampaa hakua varten)");

if (options.DownloadVideos && videos.FfmpegPath is null)
{
    // Ilman ffmpegiä videosta voidaan tarkistaa vain esikatselukuva. Suodatus vaatii luotettavan tarkistuksen.
    if (options.RejectNonCats)
    {
        Console.Error.WriteLine("Virhe: rejectNonCats vaatii ffmpegin videoiden tarkistamiseen, eikä sitä löytynyt.");
        Console.Error.WriteLine("Asenna se (winget install Gyan.FFmpeg), aseta \"ffmpegPath\" tai käytä valitsinta --no-videos.");
        return 2;
    }
    Console.WriteLine("Huom: ffmpegiä ei löytynyt; Redditin videot tallennetaan ilman ääntä " +
                      "ja tunnistetaan vain esikatselukuvasta.");
}

CatClassifier classifier;
try
{
    classifier = await CatClassifier.CreateAsync(options.YoloModel, options.YoloClassModel,
        options.Confidence, options.ClassConfidence, http);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Virhe: YOLO-mallin lataus epäonnistui: {ex.Message}");
    return 2;
}
using var classifierScope = classifier;
Console.WriteLine(
    $"Kissantunnistus: {Path.GetFileName(options.YoloModel)} (raja {Num(options.Confidence)})" +
    (classifier.UsesClassification
        ? $" + {Path.GetFileName(options.YoloClassModel)} (raja {Num(options.ClassConfidence)})"
        : "") +
    (options.RejectNonCats ? "; postaukset ilman kissaa hylätään" : "; kaikki tallennetaan"));

int totalImages = 0, totalVideos = 0, totalRejected = 0, failures = 0;

foreach (var subreddit in options.Subreddits)
{
    var subState = state.Get(subreddit);
    Console.WriteLine();
    Console.WriteLine(subState.LastPostUtc is { } last
        ? $"== r/{subreddit}: haetaan postaukset {DateTimeOffset.FromUnixTimeSeconds((long)last).ToLocalTime():yyyy-MM-dd HH:mm} jälkeen"
        : $"== r/{subreddit}: ensimmäinen haku, enintään {options.Limit} uusinta postausta");

    List<RedditPost> posts;
    try
    {
        posts = await reddit.GetNewPostsAsync(subreddit, subState.LastPostUtc, options.Limit);
    }
    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
                                   or System.Text.Json.JsonException or System.Xml.XmlException)
    {
        Console.Error.WriteLine($"  Haku epäonnistui: {ex.Message}");
        failures++;
        continue;
    }

    Console.WriteLine($"  Uusia kuva- ja videopostauksia: {posts.Count}");
    var targetDir = Path.Combine(options.OutputDir, subreddit);
    var rejectedDir = Path.Combine(options.OutputDir, "_ei-kissa", subreddit);

    // Käsitellään vanhimmasta uusimpaan, jotta tila voidaan tallentaa postaus kerrallaan.
    foreach (var post in posts.OrderBy(p => p.CreatedUtc))
    {
        var workDir = Path.Combine(Path.GetTempPath(), "kissakuva", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        try
        {
            await ProcessPostAsync(post, workDir, targetDir, rejectedDir);
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
Console.WriteLine($"Valmis. Tallennettu {totalImages} kuvaa ja {totalVideos} videota" +
    (options.RejectNonCats ? $", hylätty {totalRejected}" : "") +
    (failures > 0 ? $", virheitä {failures}" : "") + ".");
return failures > 0 ? 1 : 0;

// ---------------------------------------------------------------- käsittely

/// <summary>
/// Lataa ja tunnistaa postauksen kaikki tiedostot, päättää koko postauksesta (yksikin kissa riittää)
/// ja tulostaa postauksesta yhden rivin.
/// </summary>
async Task ProcessPostAsync(RedditPost post, string workDir, string targetDir, string rejectedDir)
{
    var kind = post.Media.Count > 1 ? $"galleria ({post.Media.Count})"
        : post.Media[0].IsVideo ? "video" : "kuva";
    var files = new List<PostFile>();
    int existing = 0, videosOff = 0, unknown = 0, errors = 0;

    for (int i = 0; i < post.Media.Count; i++)
    {
        var item = post.Media[i];
        if (item.IsVideo && !options.DownloadVideos) { videosOff++; continue; }

        var suffix = post.Media.Count > 1 ? $"_{i + 1}" : "";
        var baseName = $"{DateTimeOffset.FromUnixTimeSeconds((long)post.CreatedUtc):yyyyMMdd_HHmmss}_{post.Id}{suffix}";
        if (FileExists(targetDir, baseName)) { existing++; continue; }

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
            Console.Error.WriteLine($"  [virhe] {item.Url}: {ex.Message}");
            errors++;
            failures++;
        }
    }

    if (files.Count == 0)
    {
        var reason = errors > 0 ? "virhe"
            : existing > 0 ? "ohitettu (on jo)"
            : videosOff > 0 ? "ohitettu (videot pois)"
            : unknown > 0 ? "ohitettu (tuntematon muoto)"
            : "ohitettu";
        Report(kind, "–", reason, post.Title);
        return;
    }

    var cats = files.Where(f => f.Result.Verdict == CatVerdict.Cat).ToList();
    var summary = post.Media.Count == 1
        ? files[0].Result.Summary
        : cats.Count > 0
            ? $"kissa {cats.Count}/{files.Count}, paras {cats.MaxBy(f => f.Result.Score)!.Result.Summary}"
            : $"ei kissaa 0/{files.Count}";

    string action;
    if (options.RejectNonCats && cats.Count == 0)
    {
        if (options.KeepRejected)
        {
            Directory.CreateDirectory(rejectedDir);
            foreach (var f in files) File.Move(f.TempPath, Path.Combine(rejectedDir, f.BaseName + f.Extension), overwrite: true);
            action = "hylätty → _ei-kissa";
        }
        else
        {
            action = "hylätty";
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
        action = "tallennettu" + (files.Any(f => f.IsVideo && !f.WithAudio) ? ", ei ääntä" : "");
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
    // Esikatselukuva kuvaa postausta, joten se kelpaa vain yksittäisen videon tarkistukseen.
    CatResult? result = null;
    if (post.Media.Count == 1 && post.ThumbnailUrl is not null)
    {
        try
        {
            result = Classify(await http.GetByteArrayAsync(post.ThumbnailUrl));
        }
        catch (HttpRequestException) { /* tarkistetaan videon ruuduista */ }
    }

    var path = Path.Combine(workDir, $"video{index}.mp4");
    var withAudio = await videos.DownloadAsync(item, path, workDir);

    // Ei kissaa esikatselukuvassa: käydään läpi videon ruutuja, kunnes kissa löytyy.
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
        return new CatResult(CatVerdict.Unverifiable, 0, $"tunnistus epäonnistui ({ex.GetType().Name})");
    }
}

static void Report(string kind, string result, string action, string title)
{
    var shortTitle = title.Length > 50 ? title[..47] + "..." : title;
    Console.WriteLine($"  {kind,-13} {result,-44} {action,-21} {shortTitle}");
}

static string Num(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

static bool FileExists(string dir, string baseName) =>
    Directory.Exists(dir) && Directory.EnumerateFiles(dir, baseName + ".*").Any();

/// <summary>Ladattu ja tunnistettu tiedosto, joka odottaa postauksen päätöstä.</summary>
sealed record PostFile(string TempPath, string Extension, string BaseName, bool IsVideo, bool WithAudio, CatResult Result);
