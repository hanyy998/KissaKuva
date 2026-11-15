using System.Globalization;
using SkiaSharp;
using YoloDotNet;
using YoloDotNet.ExecutionProvider.Cpu;
using YoloDotNet.Models;

namespace KissaKuva;

public enum CatVerdict { Cat, NotCat, Unverifiable }

/// <summary>Detection result, a short summary and a score for ranking (cat confidence, 0–1).</summary>
public sealed record CatResult(CatVerdict Verdict, double Score, string Summary)
{
    public static readonly CatResult Unverifiable = new(CatVerdict.Unverifiable, 0, "could not check");
}

/// <summary>
/// Finds felines with two YOLO models (YoloDotNet, ONNX Runtime on the CPU):
/// <list type="number">
/// <item>Object detection (COCO class "cat") also finds small cats in a large image.</item>
/// <item>If no cat was found, the classification model (ImageNet) rates the whole image. The
/// probabilities of the feline classes (domestic and big cats) are summed and compared to a threshold.</item>
/// </list>
/// The image is a cat if either model says so.
/// </summary>
public sealed class CatClassifier : IDisposable
{
    public const string DefaultDetectModelFile = "yolov11s.onnx";
    public const string DefaultClassModelFile = "yolov11s-cls.onnx";

    /// <summary>Default models are downloaded on demand from YoloDotNet's test assets, pinned to one commit.</summary>
    private const string DefaultModelBaseUrl =
        "https://raw.githubusercontent.com/NickSwardh/YoloDotNet/1b10f4d491f570b8ef907c7f99421c95471e2dfe/test/assets/Models/";

    /// <summary>ImageNet feline classes 281–293 (note: "Madagascar cat" is a lemur and "polecat" a ferret).</summary>
    private static readonly HashSet<string> FelineClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "tabby", "tiger cat", "Persian cat", "Siamese cat", "Egyptian cat",
        "cougar", "lynx", "leopard", "snow leopard", "jaguar", "lion", "tiger", "cheetah",
    };

    private readonly Yolo _detector;
    private readonly Yolo? _classifier;
    private readonly double _confidence;
    private readonly double _classConfidence;

    private CatClassifier(Yolo detector, Yolo? classifier, double confidence, double classConfidence)
    {
        _detector = detector;
        _classifier = classifier;
        _confidence = confidence;
        _classConfidence = classConfidence;
    }

    public bool UsesClassification => _classifier is not null;

    /// <summary>Loads the models. Default models are downloaded if the files do not exist yet.</summary>
    /// <param name="classModelPath">Classification model; empty or null turns classification off.</param>
    public static async Task<CatClassifier> CreateAsync(string detectModelPath, string? classModelPath,
        double confidence, double classConfidence, HttpClient http)
    {
        await EnsureModelAsync(detectModelPath, http);
        var detector = new Yolo(new YoloOptions { ExecutionProvider = new CpuExecutionProvider(detectModelPath) });
        try
        {
            Yolo? classifier = null;
            if (!string.IsNullOrWhiteSpace(classModelPath))
            {
                await EnsureModelAsync(classModelPath, http);
                classifier = new Yolo(new YoloOptions { ExecutionProvider = new CpuExecutionProvider(classModelPath) });
            }
            return new CatClassifier(detector, classifier, confidence, classConfidence);
        }
        catch
        {
            detector.Dispose();
            throw;
        }
    }

    /// <summary>Checks what the image shows. Formats: JPEG, PNG, GIF (first frame) and WebP.</summary>
    public CatResult Classify(byte[] image)
    {
        using var bitmap = SKBitmap.Decode(image);
        if (bitmap is null) return CatResult.Unverifiable;

        // 1. Object detection.
        var detections = _detector.RunObjectDetection(bitmap, confidence: _confidence, iou: 0.7);
        var cats = detections.Where(d => d.Label.Name.Equals("cat", StringComparison.OrdinalIgnoreCase)).ToList();
        if (cats.Count > 0)
        {
            var best = cats.Max(d => d.Confidence);
            var count = cats.Count == 1 ? "cat" : $"{cats.Count} cats";
            return new CatResult(CatVerdict.Cat, best, $"{count} {Percent(best)}");
        }

        var others = detections
            .GroupBy(d => d.Label.Name)
            .Select(g => (Name: g.Key, Confidence: g.Max(d => d.Confidence)))
            .OrderByDescending(x => x.Confidence)
            .Take(2)
            .Select(x => $"{x.Name} {Percent(x.Confidence)}")
            .ToList();

        // 2. Classification only when detection found no cat.
        if (_classifier is not null)
        {
            var classes = _classifier.RunClassification(bitmap, classes: 1000);
            var felines = classes.Where(c => FelineClasses.Contains(Normalize(c.Label))).ToList();
            var sum = felines.Sum(c => c.Confidence);
            if (sum >= _classConfidence)
            {
                var top = felines.MaxBy(c => c.Confidence)!;
                return new CatResult(CatVerdict.Cat, sum,
                    $"feline {Percent(sum)} ({Normalize(top.Label)}, classifier)");
            }
            if (classes.MaxBy(c => c.Confidence) is { } topClass)
                others.Add($"classifier: {Normalize(topClass.Label)} {Percent(topClass.Confidence)}");
        }

        return new CatResult(CatVerdict.NotCat, 0,
            others.Count > 0 ? $"no cat ({string.Join("; ", others)})" : "no cat");
    }

    public void Dispose()
    {
        _detector.Dispose();
        _classifier?.Dispose();
    }

    private static async Task EnsureModelAsync(string modelPath, HttpClient http)
    {
        if (File.Exists(modelPath)) return;

        var file = Path.GetFileName(modelPath);
        if (!file.Equals(DefaultDetectModelFile, StringComparison.OrdinalIgnoreCase)
            && !file.Equals(DefaultClassModelFile, StringComparison.OrdinalIgnoreCase))
            throw new FileNotFoundException($"YOLO model not found: {Path.GetFullPath(modelPath)}");

        Console.WriteLine($"Downloading YOLO model: {Path.GetFullPath(modelPath)}");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(modelPath))!);
        var tmp = modelPath + ".tmp";
        using (var resp = await http.GetAsync(DefaultModelBaseUrl + file, HttpCompletionOption.ResponseHeadersRead))
        {
            resp.EnsureSuccessStatusCode();
            await using var src = await resp.Content.ReadAsStreamAsync();
            await using var dst = File.Create(tmp);
            await src.CopyToAsync(dst);
        }
        File.Move(tmp, modelPath, overwrite: true);
    }

    private static string Normalize(string label) => label.Replace('_', ' ');

    private static string Percent(double confidence) =>
        string.Create(CultureInfo.InvariantCulture, $"{confidence * 100:0}%");
}
