using System.Text.Json;

namespace KissaKuva;

/// <summary>Kanavakohtainen tila: uusimman käsitellyn postauksen aikaleima ja viimeisin ajo.</summary>
public sealed class SubredditState
{
    public double? LastPostUtc { get; set; }
    public DateTimeOffset? LastRun { get; set; }
}

public sealed class AppState
{
    public Dictionary<string, SubredditState> Subreddits { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public SubredditState Get(string subreddit)
    {
        if (!Subreddits.TryGetValue(subreddit, out var s))
            Subreddits[subreddit] = s = new SubredditState();
        return s;
    }
}

public static class StateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static AppState Load(string path)
    {
        if (!File.Exists(path)) return new AppState();
        var loaded = JsonSerializer.Deserialize<AppState>(File.ReadAllText(path), JsonOptions) ?? new AppState();
        // Palautetaan kirjainkoosta riippumaton sanakirja.
        loaded.Subreddits = new(loaded.Subreddits, StringComparer.OrdinalIgnoreCase);
        return loaded;
    }

    public static void Save(string path, AppState state)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        // Kirjoitetaan ensin väliaikaistiedostoon, ettei keskeytys korruptoi tilaa.
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(state, JsonOptions));
        File.Move(tmp, path, overwrite: true);
    }
}
