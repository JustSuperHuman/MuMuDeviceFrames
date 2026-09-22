using System.Net.Http;
using System.Text.Json;

namespace MuMuDeviceFrames;

/// <summary>
/// Fetches the frame set from the device-frames-media project, which publishes an index with hosted URLs for every
/// frame. Files are laid out exactly like its repository ({category}/{model}/{variant}/frame.png|mask.png|template.json).
/// </summary>
internal static class FrameDownloader
{
    public const string ProjectUrl = "https://github.com/jonnyjackson26/device-frames-media";
    public const string IndexUrl = "https://raw.githubusercontent.com/jonnyjackson26/device-frames-media/main/device-frames-output/index.json";
    private const string OutputFolder = "/device-frames-output/";

    public sealed record Result(int Downloaded, int UpToDate, int Failed, int Total);

    public static async Task<Result> DownloadAsync(string root, IProgress<string> progress, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("MuMuDeviceFrames/1.0 (+https://github.com/JustSuperHuman/MuMuDeviceFrames)");

        progress.Report("Fetching frame index…");
        using var index = JsonDocument.Parse(await http.GetStringAsync(IndexUrl, ct));

        var variants = new List<(string Dir, string FrameUrl, string MaskUrl, string Template)>();
        foreach (var category in index.RootElement.EnumerateObject())
        foreach (var model in category.Value.EnumerateObject())
        foreach (var variant in model.Value.EnumerateObject())
        {
            var v = variant.Value;
            string frameUrl = v.GetProperty("frame").GetString() ?? "", maskUrl = v.GetProperty("mask").GetString() ?? "";
            string path = Uri.UnescapeDataString(new Uri(frameUrl).AbsolutePath);
            int at = path.IndexOf(OutputFolder, StringComparison.Ordinal);
            if (at < 0) continue;
            // "Apple iPhone/17 Pro/Deep Blue/frame.png" -> the variant folder, with the human-readable names kept.
            string dir = Path.GetDirectoryName(path[(at + OutputFolder.Length)..])!.Replace('/', Path.DirectorySeparatorChar);

            var template = new Dictionary<string, object?>
            {
                ["frame"] = "frame.png", ["mask"] = "mask.png",
                ["screen"] = v.GetProperty("screen").Clone(), ["frameSize"] = v.GetProperty("frameSize").Clone(),
                ["hexColor"] = v.TryGetProperty("hexColor", out var color) ? color.GetString() : null,
            };
            variants.Add((dir, frameUrl, maskUrl, JsonSerializer.Serialize(template, new JsonSerializerOptions { WriteIndented = true })));
        }

        int downloaded = 0, upToDate = 0, failed = 0, done = 0;
        using var gate = new SemaphoreSlim(6);
        var tasks = variants.Select(async item =>
        {
            await gate.WaitAsync(ct);
            try
            {
                string dir = Path.Combine(root, item.Dir);
                Directory.CreateDirectory(dir);
                bool fresh = false;
                foreach (var (url, file) in new[] { (item.FrameUrl, "frame.png"), (item.MaskUrl, "mask.png") })
                {
                    string target = Path.Combine(dir, file);
                    if (File.Exists(target) && new FileInfo(target).Length > 0) continue;
                    var bytes = await http.GetByteArrayAsync(url, ct);
                    await File.WriteAllBytesAsync(target + ".part", bytes, ct);
                    File.Move(target + ".part", target, overwrite: true);
                    fresh = true;
                }
                await File.WriteAllTextAsync(Path.Combine(dir, "template.json"), item.Template, ct);
                if (fresh) Interlocked.Increment(ref downloaded); else Interlocked.Increment(ref upToDate);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Interlocked.Increment(ref failed);
                Log.Write($"Frame download failed for {item.Dir}: {ex.Message}");
            }
            finally
            {
                gate.Release();
                progress.Report($"Downloading frames… {Interlocked.Increment(ref done)}/{variants.Count}");
            }
        });
        await Task.WhenAll(tasks);
        return new Result(downloaded, upToDate, failed, variants.Count);
    }
}
