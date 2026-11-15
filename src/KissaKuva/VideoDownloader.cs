using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace KissaKuva;

/// <summary>
/// Downloads videos. Reddit's own videos (v.redd.it) are DASH: picture and sound are separate
/// files, merged with ffmpeg. Without ffmpeg only the picture track is saved.
/// </summary>
public sealed class VideoDownloader(HttpClient http, string? ffmpegPath)
{
    private static readonly XNamespace Dash = "urn:mpeg:dash:schema:mpd:2011";

    public string? FfmpegPath { get; } = ResolveFfmpeg(ffmpegPath);

    /// <summary>Downloads the video to <paramref name="outputPath"/> (.mp4). Returns true if the audio was included.</summary>
    public async Task<bool> DownloadAsync(MediaItem item, string outputPath, string workDir)
    {
        if (item.Kind == MediaKind.DirectVideo)
        {
            await DownloadToFileAsync(item.Url, outputPath);
            return true;
        }

        var (videoUrl, audioUrl) = await ResolveDashAsync(item.Url);
        var videoFile = Path.Combine(workDir, "video.mp4");
        await DownloadToFileAsync(videoUrl, videoFile);

        if (audioUrl is null)
        {
            File.Move(videoFile, outputPath, overwrite: true);
            return true; // a video without an audio track is "complete"
        }

        if (FfmpegPath is not null)
        {
            try
            {
                var audioFile = Path.Combine(workDir, "audio.mp4");
                await DownloadToFileAsync(audioUrl, audioFile);
                await RunFfmpegAsync("-y", "-loglevel", "error", "-i", videoFile, "-i", audioFile,
                    "-map", "0:v:0", "-map", "1:a:0", "-c", "copy", outputPath);
                return true;
            }
            // Any audio error: save the picture without sound, so one post cannot crash the run.
            catch (Exception) { }
        }

        File.Move(videoFile, outputPath, overwrite: true);
        return false;
    }

    /// <summary>Positions (share of the duration) where frames are taken for cat detection.</summary>
    private static readonly double[] FramePositions = [0.05, 0.175, 0.30, 0.425, 0.55, 0.675, 0.80, 0.925];

    /// <summary>
    /// Takes evenly spaced frames from the video as JPEG images (needs ffmpeg). Frames come one at a
    /// time, so the caller can stop as soon as a cat is found. If the duration is unknown, one
    /// representative frame chosen by ffmpeg is returned.
    /// </summary>
    public async IAsyncEnumerable<byte[]> ExtractFramesAsync(string videoPath, string workDir)
    {
        if (FfmpegPath is null) yield break;

        var duration = await GetDurationAsync(videoPath);
        if (duration is not > 0)
        {
            if (await ExtractFrameAsync(videoPath, workDir, null, 0) is { } single) yield return single;
            yield break;
        }

        for (var i = 0; i < FramePositions.Length; i++)
            if (await ExtractFrameAsync(videoPath, workDir, FramePositions[i] * duration.Value, i) is { } frame)
                yield return frame;
    }

    private async Task<byte[]?> ExtractFrameAsync(string videoPath, string workDir, double? seconds, int index)
    {
        var frame = Path.Combine(workDir, $"frame{index}.jpg");
        var args = new List<string> { "-y", "-loglevel", "error" };
        if (seconds is { } s) args.AddRange(["-ss", s.ToString("0.###", CultureInfo.InvariantCulture)]);
        args.AddRange(["-i", videoPath,
            "-vf", seconds is null ? "thumbnail,scale='min(1280,iw)':-2" : "scale='min(1280,iw)':-2",
            "-frames:v", "1", frame]);
        try
        {
            await RunFfmpegAsync([.. args]);
            return File.Exists(frame) ? await File.ReadAllBytesAsync(frame) : null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Reads the video's duration from ffmpeg's output ("Duration: 00:00:24.10").</summary>
    private async Task<double?> GetDurationAsync(string videoPath)
    {
        var psi = new ProcessStartInfo(FfmpegPath!) { RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in new[] { "-hide_banner", "-i", videoPath }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi);
        if (p is null) return null;
        var stderr = await p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync(); // exits with an error code, since no output file is given
        var m = Regex.Match(stderr, @"Duration:\s*(\d+):(\d+):(\d+(?:\.\d+)?)");
        return m.Success
            ? int.Parse(m.Groups[1].Value) * 3600 + int.Parse(m.Groups[2].Value) * 60
              + double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture)
            : null;
    }

    /// <summary>Reads the DASH playlist and picks the best picture and sound tracks.</summary>
    private async Task<(string Video, string? Audio)> ResolveDashAsync(string url)
    {
        var baseUri = new Uri(url.TrimEnd('/') + "/");
        var mpd = XDocument.Parse(await http.GetStringAsync(new Uri(baseUri, "DASHPlaylist.mpd")));

        (string Url, long Bandwidth)? bestVideo = null, bestAudio = null;
        foreach (var set in mpd.Descendants(Dash + "AdaptationSet"))
        {
            foreach (var rep in set.Elements(Dash + "Representation"))
            {
                var type = (string?)rep.Attribute("mimeType") ?? (string?)set.Attribute("mimeType")
                           ?? (string?)set.Attribute("contentType") ?? "";
                var baseUrl = rep.Element(Dash + "BaseURL")?.Value.Trim();
                if (string.IsNullOrEmpty(baseUrl)) continue;
                var bw = (long?)rep.Attribute("bandwidth") ?? 0;
                var abs = new Uri(baseUri, baseUrl).ToString();

                if (type.StartsWith("video") && (bestVideo is null || bw > bestVideo.Value.Bandwidth))
                    bestVideo = (abs, bw);
                else if (type.StartsWith("audio") && (bestAudio is null || bw > bestAudio.Value.Bandwidth))
                    bestAudio = (abs, bw);
            }
        }

        if (bestVideo is null) throw new InvalidOperationException("No video track found in the DASH playlist.");
        return (bestVideo.Value.Url, bestAudio?.Url);
    }

    private async Task DownloadToFileAsync(string url, string path)
    {
        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        resp.EnsureSuccessStatusCode();
        await using var src = await resp.Content.ReadAsStreamAsync();
        await using var dst = File.Create(path);
        await src.CopyToAsync(dst);
    }

    private async Task RunFfmpegAsync(params string[] args)
    {
        var psi = new ProcessStartInfo(FfmpegPath!) { RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Starting ffmpeg failed.");
        var stderr = await p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        if (p.ExitCode != 0) throw new InvalidOperationException($"ffmpeg failed: {stderr.Trim()}");
    }

    private static string? ResolveFfmpeg(string? configured)
    {
        if (!string.IsNullOrEmpty(configured)) return File.Exists(configured) ? configured : null;
        var names = OperatingSystem.IsWindows() ? new[] { "ffmpeg.exe" } : ["ffmpeg"];
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            foreach (var name in names)
            {
                try
                {
                    var candidate = Path.Combine(dir.Trim('"'), name);
                    if (File.Exists(candidate)) return candidate;
                }
                catch (ArgumentException) { }
            }

        // winget installs ffmpeg per user; PATH only updates in new terminals.
        var localAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        if (OperatingSystem.IsWindows() && !string.IsNullOrEmpty(localAppData))
        {
            var winget = Path.Combine(localAppData, "Microsoft", "WinGet");
            var link = Path.Combine(winget, "Links", "ffmpeg.exe");
            if (File.Exists(link)) return link;
            var packages = Path.Combine(winget, "Packages");
            try
            {
                if (Directory.Exists(packages))
                    foreach (var dir in Directory.EnumerateDirectories(packages, "*FFmpeg*"))
                        if (Directory.EnumerateFiles(dir, "ffmpeg.exe", SearchOption.AllDirectories).FirstOrDefault() is { } exe)
                            return exe;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return null;
    }
}
