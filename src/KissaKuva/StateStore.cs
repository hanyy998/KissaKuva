using System.Text.Json;

namespace KissaKuva;

/// <summary>Per-subreddit state: the timestamp of the newest handled post and the last run.</summary>
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
        // Restore the case-insensitive dictionary.
        loaded.Subreddits = new(loaded.Subreddits, StringComparer.OrdinalIgnoreCase);
        return loaded;
    }

    public static void Save(string path, AppState state)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        // Write to a temp file first, so an interruption cannot corrupt the state.
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(state, JsonOptions));
        File.Move(tmp, path, overwrite: true);
    }
}
