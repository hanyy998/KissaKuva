using System.Text.Json;

namespace KissaKuva;

/// <summary>Shape of the config file (kissakuva.json).</summary>
public sealed class ConfigFile
{
    public List<string> Subreddits { get; set; } = [];
    public string? OutputDir { get; set; }
    public string? StatePath { get; set; }
    public int? Limit { get; set; }
    public bool? RejectNonCats { get; set; }
    public string? YoloModel { get; set; }
    public string? YoloClassModel { get; set; }
    public double? Confidence { get; set; }
    public double? ClassConfidence { get; set; }
    public bool? KeepRejected { get; set; }
    public bool? DownloadVideos { get; set; }
    public string? FfmpegPath { get; set; }
}

public sealed class Options
{
    public const string DefaultConfigPath = "kissakuva.json";

    public List<string> Subreddits { get; } = [];
    public string ConfigPath { get; private set; } = DefaultConfigPath;
    public string OutputDir { get; private set; } = "images";
    public string StatePath { get; private set; } = "kissakuva-state.json";
    public int Limit { get; private set; } = 50;
    public bool RejectNonCats { get; private set; }
    public bool KeepRejected { get; private set; }
    public bool DownloadVideos { get; private set; } = true;
    public string YoloModel { get; private set; } = Path.Combine("models", CatClassifier.DefaultDetectModelFile);
    /// <summary>Classification model; empty = classification off.</summary>
    public string YoloClassModel { get; private set; } = Path.Combine("models", CatClassifier.DefaultClassModelFile);
    public double Confidence { get; private set; } = 0.4;
    public double ClassConfidence { get; private set; } = 0.5;
    public string? FfmpegPath { get; private set; }
    public bool ShowHelp { get; private set; }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Reads the config file and applies the command-line options on top of it.</summary>
    public static Options Parse(string[] args)
    {
        var o = new Options();
        var overrides = new List<Action>();

        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"Option {a} needs a value.");

            switch (a)
            {
                case "-h" or "--help" or "/?":
                    o.ShowHelp = true;
                    return o;
                case "-c" or "--config":
                    o.ConfigPath = Next();
                    break;
                case "-o" or "--out":
                    { var v = Next(); overrides.Add(() => o.OutputDir = v); }
                    break;
                case "-s" or "--state":
                    { var v = Next(); overrides.Add(() => o.StatePath = v); }
                    break;
                case "-n" or "--limit":
                    {
                        if (!int.TryParse(Next(), out var n) || n < 1)
                            throw new ArgumentException("--limit must be a positive integer.");
                        overrides.Add(() => o.Limit = n);
                    }
                    break;
                case "--reject-non-cats":
                    overrides.Add(() => o.RejectNonCats = true);
                    break;
                case "--no-reject":
                    overrides.Add(() => o.RejectNonCats = false);
                    break;
                case "--confidence":
                    {
                        var c = ParseConfidence(Next(), "--confidence");
                        overrides.Add(() => o.Confidence = c);
                    }
                    break;
                case "--class-confidence":
                    {
                        var c = ParseConfidence(Next(), "--class-confidence");
                        overrides.Add(() => o.ClassConfidence = c);
                    }
                    break;
                case "--class-model":
                    { var v = Next(); overrides.Add(() => o.YoloClassModel = v); }
                    break;
                case "--keep-rejected":
                    overrides.Add(() => o.KeepRejected = true);
                    break;
                case "--no-videos":
                    overrides.Add(() => o.DownloadVideos = false);
                    break;
                case "--model":
                    { var v = Next(); overrides.Add(() => o.YoloModel = v); }
                    break;
                case "--ffmpeg":
                    { var v = Next(); overrides.Add(() => o.FfmpegPath = v); }
                    break;
                default:
                    throw new ArgumentException(a.StartsWith('-')
                        ? $"Unknown option: {a}"
                        : $"Unknown argument: {a}. Subreddits are set in the config file ({DefaultConfigPath}).");
            }
        }

        o.LoadConfig();
        foreach (var apply in overrides) apply();

        if (o.Subreddits.Count == 0)
            throw new ArgumentException($"The config file {o.ConfigPath} lists no subreddits (\"subreddits\").");
        return o;
    }

    private void LoadConfig()
    {
        if (!File.Exists(ConfigPath))
            throw new ArgumentException($"Config file not found: {Path.GetFullPath(ConfigPath)}");

        ConfigFile cfg;
        try
        {
            cfg = JsonSerializer.Deserialize<ConfigFile>(File.ReadAllText(ConfigPath), JsonOptions) ?? new();
        }
        catch (JsonException ex)
        {
            throw new ArgumentException($"Config file {ConfigPath} is invalid: {ex.Message}");
        }

        // Relative paths in the file are relative to the config file's folder.
        var baseDir = Path.GetDirectoryName(Path.GetFullPath(ConfigPath))!;
        string Resolve(string p) => Path.IsPathRooted(p) ? p : Path.Combine(baseDir, p);

        foreach (var s in cfg.Subreddits) AddSubreddit(s);
        OutputDir = Resolve(cfg.OutputDir ?? OutputDir);
        StatePath = Resolve(cfg.StatePath ?? StatePath);
        if (cfg.Limit is { } limit)
        {
            if (limit < 1) throw new ArgumentException("\"limit\" must be a positive integer.");
            Limit = limit;
        }
        RejectNonCats = cfg.RejectNonCats ?? RejectNonCats;
        YoloModel = Resolve(cfg.YoloModel ?? YoloModel);
        // An empty yoloClassModel turns classification off; otherwise the path resolves like the others.
        if (cfg.YoloClassModel is { } classModel)
            YoloClassModel = string.IsNullOrWhiteSpace(classModel) ? "" : Resolve(classModel);
        else
            YoloClassModel = Resolve(YoloClassModel);
        if (cfg.Confidence is { } conf)
            Confidence = conf is > 0 and < 1
                ? conf
                : throw new ArgumentException("\"confidence\" must be a number between 0 and 1 (e.g. 0.4).");
        if (cfg.ClassConfidence is { } classConf)
            ClassConfidence = classConf is > 0 and < 1
                ? classConf
                : throw new ArgumentException("\"classConfidence\" must be a number between 0 and 1 (e.g. 0.5).");
        KeepRejected = cfg.KeepRejected ?? KeepRejected;
        DownloadVideos = cfg.DownloadVideos ?? DownloadVideos;
        FfmpegPath = cfg.FfmpegPath ?? FfmpegPath;
    }

    private static double ParseConfidence(string s, string name) =>
        double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var c)
        && c is > 0 and < 1
            ? c
            : throw new ArgumentException($"{name} must be a number between 0 and 1 (e.g. 0.4).");

    private void AddSubreddit(string raw)
    {
        // Accepts: /r/aww, r/aww, aww, https://www.reddit.com/r/aww/
        var s = raw.Trim().TrimEnd('/');
        var idx = s.LastIndexOf("r/", StringComparison.OrdinalIgnoreCase);
        if (idx >= 0) s = s[(idx + 2)..];
        s = s.Trim('/');

        if (s.Length == 0 || !s.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
            throw new ArgumentException($"Invalid subreddit in the config file: {raw}");

        // A set: each subreddit once, ignoring case.
        if (!Subreddits.Contains(s, StringComparer.OrdinalIgnoreCase))
            Subreddits.Add(s);
    }

    public static void PrintUsage()
    {
        Console.WriteLine("""
            KissaKuva - downloads the newest cat pictures and videos from Reddit

            Usage:
              KissaKuva [options]

            Subreddits and settings are read from the config file (default: kissakuva.json):

              {
                "subreddits": [ "/r/aww", "/r/cats" ],
                "outputDir": "images",
                "limit": 50,
                "rejectNonCats": true,
                "downloadVideos": true
              }

            Files are saved to <out>/<yyyy-MM-dd>/<subreddit>/ (the download day).

            Command-line options override the file:
              -c, --config <file>      Config file (default: kissakuva.json)
              -o, --out <folder>       Output folder
              -s, --state <file>       State file for the last fetches
              -n, --limit <number>     At most this many posts per subreddit per run
                  --reject-non-cats    Reject images and videos with no detected cat
                  --no-reject          Save everything (the detection result is still shown)
                  --model <path>       YOLO detection model (default: models/yolov11s.onnx)
                  --class-model <path>  YOLO classification model (default: models/yolov11s-cls.onnx;
                                       empty "" = no classification)
                  --confidence <0-1>   Minimum confidence of the detection model (default: 0.4)
                  --class-confidence <0-1>  Threshold for the sum of the feline classes (default: 0.5)
                  --keep-rejected      Save rejected files to <out>/_rejected/<yyyy-MM-dd>/<subreddit>/
                  --no-videos          Do not download videos
                  --ffmpeg <path>      Path to ffmpeg (optional)
              -h, --help               Show this help
            """);
    }
}
