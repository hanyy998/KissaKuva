# KissaKuva

Downloads the newest cat pictures and videos from subreddits. The program remembers the newest
handled post of each subreddit, so the next run only fetches posts that came after it. YOLO
detection tells for every post whether it shows a cat, and other posts can be rejected.

## Usage

```powershell
dotnet run --project src/KissaKuva                                   # reads kissakuva.json
dotnet run --project src/KissaKuva -- -c my-subreddits.json          # another config file
dotnet run --project src/KissaKuva -- --reject-non-cats --no-videos  # override file settings
```

## Output folders

Files are saved by download day (the local date of the run):

```
images/
  2026-11-15/
    aww/
      20261115_050347_1xwmyjy.mp4
      20261115_073256_1xwphi1.jpg
    cats/
      ...
  _rejected/              (only with keepRejected)
    2026-11-15/
      aww/
        ...
```

A file name starts with the post's creation time in UTC (`yyyyMMdd_HHmmss`), followed by the post
id, and for galleries the image number (`_2`). A post that is already saved on any day is skipped.

## Config file

Subreddits and settings are in [kissakuva.json](kissakuva.json) (by default in the current folder;
pass another file with `-c`). The file may contain comments.

```jsonc
{
  "subreddits": [ "/r/aww", "/r/cats", "/r/catpictures" ],
  "outputDir": "images",
  "statePath": "kissakuva-state.json",
  "limit": 50,
  "downloadVideos": true,
  "ffmpegPath": null,
  "rejectNonCats": false,
  "yoloModel": "models/yolov11s.onnx",
  "yoloClassModel": "models/yolov11s-cls.onnx",
  "confidence": 0.4,
  "classConfidence": 0.5,
  "keepRejected": false
}
```

| Field | Meaning | Default |
|---|---|---|
| `subreddits` | Subreddits as a set. `/r/aww`, `r/aww` and `aww` all work; the same subreddit listed twice (in any case) is handled once. **Required.** | – |
| `outputDir` | Output folder; files go to `<outputDir>/<yyyy-MM-dd>/<subreddit>/` | `images` |
| `statePath` | State file that stores the last fetch | `kissakuva-state.json` |
| `limit` | At most this many posts per subreddit per run | `50` |
| `downloadVideos` | Download videos too | `true` |
| `ffmpegPath` | Path to ffmpeg; if empty, it is searched in PATH and in winget's install folder | – |
| `rejectNonCats` | Reject posts with no detected cat (needs ffmpeg when videos are downloaded) | `false` |
| `yoloModel` | YOLO detection model (ONNX). The default model is downloaded automatically if missing | `models/yolov11s.onnx` |
| `yoloClassModel` | YOLO classification model (ONNX); empty = no classification | `models/yolov11s-cls.onnx` |
| `confidence` | Minimum confidence of the detection model (0–1) | `0.4` |
| `classConfidence` | Threshold for the sum of the feline classes in the classification model (0–1) | `0.5` |
| `keepRejected` | Save rejected posts to `<outputDir>/_rejected/<yyyy-MM-dd>/<subreddit>/` | `false` |

Relative paths are relative to the config file's folder.

Command-line options override the file: `-o/--out`, `-s/--state`, `-n/--limit`,
`--reject-non-cats`/`--no-reject`, `--model`, `--class-model`, `--confidence`, `--class-confidence`,
`--keep-rejected`, `--no-videos`, `--ffmpeg`. Help: `--help`.

## Videos

Downloads Reddit's own videos (`v.redd.it`), direct `.mp4` links, Imgur `.gifv` videos and
animated gallery images. Videos are saved as `.mp4`.

In Reddit videos the picture and the sound are separate files, which the program merges into one
`.mp4` with **ffmpeg**. ffmpeg is looked up in PATH, in the `ffmpegPath` setting or in winget's
install folder. Without ffmpeg the video is saved without sound (the program says so), and
`rejectNonCats` always needs it when videos are downloaded. Install:

```powershell
winget install Gyan.FFmpeg
```

The program also finds a winget-installed ffmpeg from a terminal that was open before the install.

## State (last fetch)

`kissakuva-state.json` holds, per subreddit, the timestamp of the newest handled post
(`LastPostUtc`) and the time of the last run. Posts are handled from oldest to newest and the state
is saved after each post, so an interrupted run continues from the right place next time. Delete
the file (or a subreddit's entry) to start over.

## Reddit access

* **Without credentials** the program uses Reddit's public RSS feed. It works right away, but Reddit
  rate-limits it tightly (the program waits automatically), and only direct image and video links
  are found; galleries are left out.
* **Recommended:** create a Reddit app at <https://www.reddit.com/prefs/apps> (type *script*,
  redirect uri e.g. `http://localhost:8080`) and set the environment variables:

  ```powershell
  $env:REDDIT_CLIENT_ID = "..."
  $env:REDDIT_CLIENT_SECRET = "..."
  ```

  Then the official OAuth API is used: it is faster and also supports gallery posts and NSFW
  filtering.

## Cat detection (YOLO)

Every feline counts as a cat: domestic cats as well as tiger, lion, leopard, snow leopard, jaguar,
cheetah, lynx and cougar. Detection runs locally with the
[YoloDotNet](https://github.com/NickSwardh/YoloDotNet) library and two YOLO models (ONNX, on the
CPU). No API keys are needed.

1. The **detection model** (`yoloModel`, COCO classes) looks for objects in the image. If it finds a
   cat with at least `confidence`, the image is accepted. It also finds small cats in a large image.
2. The **classification model** (`yoloClassModel`, ImageNet classes) runs only when no cat was found.
   It rates the whole image. The probabilities of the feline classes (tabby, Persian, Siamese,
   tiger, lion, snow leopard…) are summed, and the image is accepted if the sum is at least
   `classConfidence`.

The default models `models/yolov11s.onnx` (38 MB) and `models/yolov11s-cls.onnx` (27 MB) are
downloaded automatically on the first run from YoloDotNet's GitHub repository (pinned to one
commit). Set `yoloClassModel` to empty (`""`) to turn classification off. Your own models must be
exported to ONNX, e.g. `yolo export model=yolo11s.pt format=onnx opset=17`. Note that the
Ultralytics models are licensed under AGPL-3.0.

* **Images** are checked as they are (the first frame of a GIF).
* **Videos**: first the post's preview image, then at most eight evenly spaced frames (from 5% to
  92.5% of the duration). The check stops at the first cat. Taking frames needs ffmpeg, so
  `rejectNonCats` always needs it when videos are downloaded.
* **Galleries** are decided as a whole: if any image shows a cat, the whole gallery is saved.

The result is shown for every new post on one line:

```
  image         cat 93%                                      saved                 Should I neuter my indoor cat?
  image         feline 61% (Egyptian cat, classifier)        saved                 Blaze doesn't sit
  gallery (5)   cat 3/5, best cat 92%                        saved                 Molly
  video         no cat (dog 92%; classifier: Ibizan hound 50%) rejected            "Does your dog bite?"
```

With `rejectNonCats` on, posts with no detected cat are rejected: they are skipped, or with
`keepRejected` saved to `_rejected`. Otherwise everything is saved and the result is for
information only. A lower threshold finds more cats but also makes more wrong detections.

The models do not recognise everything, so `rejectNonCats` can also reject cat pictures. In tests,
cats behind the bars of a carrier, a cat's tail alone and a snow leopard went undetected.

## Layout

```
KissaKuva.sln
kissakuva.json        config file (subreddits and settings)
src/KissaKuva/
  Program.cs          main program
  Options.cs          config file and command-line options
  RedditClient.cs     Reddit fetching (OAuth/JSON or RSS)
  VideoDownloader.cs  video download (DASH + ffmpeg)
  StateStore.cs       reading and saving the state
  CatClassifier.cs    cat detection (YoloDotNet)
  ImageFormat.cs      image format detection
```
