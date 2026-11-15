using System.Text.Json;

namespace KissaKuva;

/// <summary>Parametritiedoston (kissakuva.json) rakenne.</summary>
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
    public string OutputDir { get; private set; } = "kuvat";
    public string StatePath { get; private set; } = "kissakuva-state.json";
    public int Limit { get; private set; } = 50;
    public bool RejectNonCats { get; private set; }
    public bool KeepRejected { get; private set; }
    public bool DownloadVideos { get; private set; } = true;
    public string YoloModel { get; private set; } = Path.Combine("models", CatClassifier.DefaultDetectModelFile);
    /// <summary>Luokittelumalli; tyhjä = luokittelu pois käytöstä.</summary>
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

    /// <summary>Lukee parametritiedoston ja soveltaa komentorivin valitsimet sen päälle.</summary>
    public static Options Parse(string[] args)
    {
        var o = new Options();
        var overrides = new List<Action>();

        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"Valitsin {a} vaatii arvon.");

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
                            throw new ArgumentException("--limit pitää olla positiivinen kokonaisluku.");
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
                        ? $"Tuntematon valitsin: {a}"
                        : $"Tuntematon parametri: {a}. Kanavat määritellään parametritiedostossa ({DefaultConfigPath}).");
            }
        }

        o.LoadConfig();
        foreach (var apply in overrides) apply();

        if (o.Subreddits.Count == 0)
            throw new ArgumentException($"Parametritiedostossa {o.ConfigPath} ei ole yhtään kanavaa (\"subreddits\").");
        return o;
    }

    private void LoadConfig()
    {
        if (!File.Exists(ConfigPath))
            throw new ArgumentException($"Parametritiedostoa ei löydy: {Path.GetFullPath(ConfigPath)}");

        ConfigFile cfg;
        try
        {
            cfg = JsonSerializer.Deserialize<ConfigFile>(File.ReadAllText(ConfigPath), JsonOptions) ?? new();
        }
        catch (JsonException ex)
        {
            throw new ArgumentException($"Parametritiedosto {ConfigPath} on virheellinen: {ex.Message}");
        }

        // Tiedostossa annetut suhteelliset polut tulkitaan suhteessa parametritiedoston hakemistoon.
        var baseDir = Path.GetDirectoryName(Path.GetFullPath(ConfigPath))!;
        string Resolve(string p) => Path.IsPathRooted(p) ? p : Path.Combine(baseDir, p);

        foreach (var s in cfg.Subreddits) AddSubreddit(s);
        OutputDir = Resolve(cfg.OutputDir ?? OutputDir);
        StatePath = Resolve(cfg.StatePath ?? StatePath);
        if (cfg.Limit is { } limit)
        {
            if (limit < 1) throw new ArgumentException("\"limit\" pitää olla positiivinen kokonaisluku.");
            Limit = limit;
        }
        RejectNonCats = cfg.RejectNonCats ?? RejectNonCats;
        YoloModel = Resolve(cfg.YoloModel ?? YoloModel);
        // Tyhjä yoloClassModel kytkee luokittelun pois; muuten polku tulkitaan kuten muutkin.
        if (cfg.YoloClassModel is { } classModel)
            YoloClassModel = string.IsNullOrWhiteSpace(classModel) ? "" : Resolve(classModel);
        else
            YoloClassModel = Resolve(YoloClassModel);
        if (cfg.Confidence is { } conf)
            Confidence = conf is > 0 and < 1
                ? conf
                : throw new ArgumentException("\"confidence\" pitää olla luku väliltä 0–1 (esim. 0.4).");
        if (cfg.ClassConfidence is { } classConf)
            ClassConfidence = classConf is > 0 and < 1
                ? classConf
                : throw new ArgumentException("\"classConfidence\" pitää olla luku väliltä 0–1 (esim. 0.5).");
        KeepRejected = cfg.KeepRejected ?? KeepRejected;
        DownloadVideos = cfg.DownloadVideos ?? DownloadVideos;
        FfmpegPath = cfg.FfmpegPath ?? FfmpegPath;
    }

    private static double ParseConfidence(string s, string name) =>
        double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var c)
        && c is > 0 and < 1
            ? c
            : throw new ArgumentException($"{name} pitää olla luku väliltä 0–1 (esim. 0.4).");

    private void AddSubreddit(string raw)
    {
        // Hyväksyy muodot: /r/aww, r/aww, aww, https://www.reddit.com/r/aww/
        var s = raw.Trim().TrimEnd('/');
        var idx = s.LastIndexOf("r/", StringComparison.OrdinalIgnoreCase);
        if (idx >= 0) s = s[(idx + 2)..];
        s = s.Trim('/');

        if (s.Length == 0 || !s.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
            throw new ArgumentException($"Virheellinen alireddit parametritiedostossa: {raw}");

        // Joukko: sama kanava vain kerran (kirjainkoolla ei väliä).
        if (!Subreddits.Contains(s, StringComparer.OrdinalIgnoreCase))
            Subreddits.Add(s);
    }

    public static void PrintUsage()
    {
        Console.WriteLine("""
            KissaKuva - lataa uusimmat kissakuvat ja -videot Redditistä

            Käyttö:
              KissaKuva [valitsimet]

            Kanavat ja asetukset luetaan parametritiedostosta (oletus: kissakuva.json):

              {
                "subreddits": [ "/r/aww", "/r/cats" ],
                "outputDir": "kuvat",
                "limit": 50,
                "rejectNonCats": true,
                "downloadVideos": true
              }

            Komentorivin valitsimet ohittavat tiedoston asetukset:
              -c, --config <tiedosto>  Parametritiedosto (oletus: kissakuva.json)
              -o, --out <hakemisto>    Tallennushakemisto
              -s, --state <tiedosto>   Tilatiedosto viimeisille hauille
              -n, --limit <luku>       Enintään näin monta postausta per kanava per ajo
                  --reject-non-cats    Hylkää kuvat ja videot, joissa ei tunnistettu kissaa
                  --no-reject          Tallenna kaikki (tunnistustulos näytetään silti)
                  --model <polku>      YOLO-tunnistusmalli (oletus: models/yolov11s.onnx)
                  --class-model <polku>  YOLO-luokittelumalli (oletus: models/yolov11s-cls.onnx;
                                       tyhjä "" = ei luokittelua)
                  --confidence <0-1>   Tunnistusmallin vähimmäisvarmuus (oletus: 0.4)
                  --class-confidence <0-1>  Kissaeläinluokkien summan raja (oletus: 0.5)
                  --keep-rejected      Tallenna hylätyt hakemistoon <out>/_ei-kissa
                  --no-videos          Älä lataa videoita
                  --ffmpeg <polku>     ffmpeg-ohjelman polku (valinnainen)
              -h, --help               Näytä tämä ohje
            """);
    }
}
