using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace MuMuDeviceFrames;

internal sealed record AppUpdate(Version Version, string Tag, Uri DownloadUrl, long Size, string Sha256)
{
    public string DisplayVersion => AppUpdates.DisplayVersion(Version);
}

/// <summary>Stable GitHub releases only; no credentials or additional updater runtime are needed.</summary>
internal sealed class AppUpdates : IDisposable
{
    internal const string Repository = "https://github.com/JustSuperHuman/MuMuDeviceFrames";
    internal const string LatestReleaseApi = "https://api.github.com/repos/JustSuperHuman/MuMuDeviceFrames/releases/latest";
    internal const string InstallerName = "MuMuDeviceFrames-Setup.exe";
    private const long MaximumDownloadSize = 256 * 1024 * 1024;
    private readonly HttpClient _http;

    public static Version CurrentVersion => Normalize(typeof(AppUpdates).Assembly.GetName().Version!);
    public static string DisplayVersion(Version version) => version.Revision > 0 ? version.ToString(4) : version.ToString(3);
    private static Version Normalize(Version version) => new(version.Major, version.Minor, Math.Max(0, version.Build), Math.Max(0, version.Revision));

    public AppUpdates(HttpMessageHandler? handler = null)
    {
        _http = handler == null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = Timeout.InfiniteTimeSpan;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"MuMuDeviceFrames/{DisplayVersion(CurrentVersion)}");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        _http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2026-03-10");
    }

    public async Task<AppUpdate?> CheckAsync(Version current, CancellationToken cancellation)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var response = await _http.GetAsync(LatestReleaseApi, timeout.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return ParseRelease(await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false), current);
    }

    internal static AppUpdate? ParseRelease(string json, Version current)
    {
        using var document = JsonDocument.Parse(json);
        var release = document.RootElement;
        if (release.GetProperty("draft").GetBoolean() || release.GetProperty("prerelease").GetBoolean()) return null;
        string tag = release.GetProperty("tag_name").GetString() ?? "";
        string number = tag.StartsWith('v') || tag.StartsWith('V') ? tag[1..] : tag;
        if (tag.Length > 64 || number.Any(c => !char.IsAsciiDigit(c) && c != '.') || !Version.TryParse(number, out var version))
            throw new InvalidDataException("The release has an unsupported version number.");
        version = Normalize(version);
        if (version <= Normalize(current)) return null;

        foreach (var asset in release.GetProperty("assets").EnumerateArray())
        {
            if (asset.GetProperty("name").GetString() != InstallerName || asset.GetProperty("state").GetString() != "uploaded") continue;
            var expected = new Uri($"{Repository}/releases/download/{Uri.EscapeDataString(tag)}/{InstallerName}");
            if (!Uri.TryCreate(asset.GetProperty("browser_download_url").GetString(), UriKind.Absolute, out var url) || url.AbsoluteUri != expected.AbsoluteUri)
                throw new InvalidDataException("The update installer is not hosted in the official release.");
            long size = asset.GetProperty("size").GetInt64();
            if (size <= 0 || size > MaximumDownloadSize) throw new InvalidDataException("The update installer has an unexpected size.");
            string? digest = asset.TryGetProperty("digest", out var value) ? value.GetString() : null;
            if (digest == null || !digest.StartsWith("sha256:", StringComparison.Ordinal) || digest.Length != 71 || !digest.Skip(7).All(char.IsAsciiHexDigit))
                throw new InvalidDataException("The release's SHA-256 checksum is not ready. Please try again later.");
            return new(version, tag, url, size, digest[7..]);
        }
        throw new InvalidDataException("The new release's installer is not ready. Please try again later.");
    }

    /// <summary>Download to a unique staging file; only expose an executable after size and SHA-256 both match.</summary>
    internal async Task<string> DownloadAsync(AppUpdate update, string stagingDirectory, IProgress<int>? progress, CancellationToken cancellation)
    {
        Directory.CreateDirectory(stagingDirectory);
        string path = Path.Combine(stagingDirectory, $"MuMuDeviceFrames-Setup-{update.DisplayVersion}-{Guid.NewGuid():N}.exe");
        string partial = path + ".download";
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(TimeSpan.FromMinutes(10));
            using var response = await _http.GetAsync(update.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is { } length && length != update.Size)
                throw new InvalidDataException("The downloaded installer has an unexpected size.");
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long received = 0;
            int lastPercent = -1;
            await using (var input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false))
            await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
            {
                byte[] buffer = new byte[81920];
                int count;
                while ((count = await input.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) != 0)
                {
                    received += count;
                    if (received > update.Size || received > MaximumDownloadSize) throw new InvalidDataException("The update download exceeded its expected size.");
                    hash.AppendData(buffer, 0, count);
                    await output.WriteAsync(buffer.AsMemory(0, count), timeout.Token).ConfigureAwait(false);
                    int percent = (int)(100 * received / update.Size);
                    if (percent != lastPercent) { progress?.Report(percent); lastPercent = percent; }
                }
            }
            if (received != update.Size || !CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), Convert.FromHexString(update.Sha256)))
                throw new InvalidDataException("The update installer failed its SHA-256 verification. Please download it again.");
            cancellation.ThrowIfCancellationRequested();
            File.Move(partial, path);
            return path;
        }
        catch
        {
            File.Delete(partial);
            throw;
        }
    }

    internal static ProcessStartInfo InstallerStartInfo(string installer, string installDirectory, string logPath)
    {
        var start = new ProcessStartInfo(installer) { UseShellExecute = true };
        foreach (string argument in new[] { "/SP-", "/SILENT", "/NORESTART", "/UPDATE=1", $"/DIR={Path.TrimEndingDirectorySeparator(Path.GetFullPath(installDirectory))}", $"/LOG={logPath}" })
            start.ArgumentList.Add(argument);
        return start;
    }

    internal static string StagingDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MuMuDeviceFrames", "Updates");

    internal static void CleanOldDownloads()
    {
        try
        {
            if (!Directory.Exists(StagingDirectory)) return;
            foreach (string file in Directory.EnumerateFiles(StagingDirectory, "MuMuDeviceFrames-Setup-*", SearchOption.TopDirectoryOnly))
                if (File.GetLastWriteTimeUtc(file) < DateTime.UtcNow.AddDays(-7)) File.Delete(file);
        }
        catch (Exception ex) { Log.Write($"Update cache cleanup: {ex.Message}"); }
    }

    public void Dispose() => _http.Dispose();
}
