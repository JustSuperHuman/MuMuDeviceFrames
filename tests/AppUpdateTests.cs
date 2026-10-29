using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MuMuDeviceFrames;

internal static class AppUpdateTests
{
    private static readonly byte[] Payload = Encoding.UTF8.GetBytes("test installer bytes");
    private static readonly string Digest = Convert.ToHexString(SHA256.HashData(Payload));

    private static string Release(string tag = "v2.0.0", bool draft = false, bool prerelease = false,
        string? url = null, string? digest = null, bool installer = true) => JsonSerializer.Serialize(new
    {
        tag_name = tag, draft, prerelease,
        assets = installer ? new[] { new {
            name = AppUpdates.InstallerName, state = "uploaded", size = Payload.Length,
            browser_download_url = url ?? $"{AppUpdates.Repository}/releases/download/{tag}/{AppUpdates.InstallerName}",
            digest = digest ?? $"sha256:{Digest}"
        } } : []
    });

    public static void Run(Action<bool, string> check) => RunAsync(check).GetAwaiter().GetResult();

    private static async Task RunAsync(Action<bool, string> check)
    {
        var current = new Version(1, 4, 0, 0);
        check(AppUpdates.ParseRelease(Release(), current)?.Version == new Version(2, 0, 0, 0), "A newer stable app release is offered");
        check(AppUpdates.ParseRelease(Release("v1.10.0"), new Version(1, 9, 0)) != null, "Update versions compare numerically");
        foreach (string tag in new[] { "v1.4.0", "v1.4", "1.4.0.0", "v1.3.2" })
            check(AppUpdates.ParseRelease(Release(tag), current) == null, $"No reinstall or downgrade for {tag}");
        check(AppUpdates.ParseRelease(Release(draft: true), current) == null, "Draft updates are ignored");
        check(AppUpdates.ParseRelease(Release(prerelease: true), current) == null, "Prerelease updates are ignored");
        void Rejected(string json, string name)
        {
            try { AppUpdates.ParseRelease(json, current); throw new Exception(name); }
            catch (InvalidDataException) { check(true, name); }
        }
        Rejected(Release("v2.0.0-preview"), "Unexpected version formats are rejected");
        Rejected(Release(installer: false), "An incomplete release cannot be installed");
        Rejected(Release(url: "https://example.org/setup.exe"), "Installers from another host are rejected");
        Rejected(Release(url: $"{AppUpdates.Repository}/releases/download/v1.0.0/{AppUpdates.InstallerName}"), "An installer for the wrong version is rejected");
        Rejected(Release(digest: "sha256:abc"), "An update without a usable SHA-256 is rejected");

        string folder = Path.Combine(Path.GetTempPath(), "deviceframes-update-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var update = AppUpdates.ParseRelease(Release(), current)!;
            using (var updates = new AppUpdates(new Handler(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(Payload) })))
            {
                string file = await updates.DownloadAsync(update, folder, null, CancellationToken.None);
                check(File.ReadAllBytes(file).SequenceEqual(Payload) && Directory.GetFiles(folder).Length == 1,
                    "A complete matching installer is staged with no partial file");
                File.Delete(file);
            }
            async Task RejectDownload(AppUpdate candidate, HttpContent content, string name)
            {
                using var updates = new AppUpdates(new Handler(_ => new(HttpStatusCode.OK) { Content = content }));
                try { await updates.DownloadAsync(candidate, folder, null, CancellationToken.None); throw new Exception(name); }
                catch (InvalidDataException) { check(Directory.GetFiles(folder).Length == 0, name); }
            }
            await RejectDownload(update with { Sha256 = new string('0', 64) }, new ByteArrayContent(Payload),
                "A corrupt download is rejected and removed");
            var truncated = new ByteArrayContent(Payload[..^1]);
            truncated.Headers.ContentLength = Payload.Length;
            await RejectDownload(update, truncated, "A truncated body is rejected and removed");
            await RejectDownload(update, new ByteArrayContent([.. Payload, 0]), "An oversized installer is rejected and removed");

            using var cancel = new CancellationTokenSource();
            using (var updates = new AppUpdates(new Handler(_ => new(HttpStatusCode.OK) { Content = new StreamContent(new CancellingStream(cancel)) })))
            {
                try { await updates.DownloadAsync(update, folder, null, cancel.Token); throw new Exception("Cancellation was ignored"); }
                catch (OperationCanceledException) { check(Directory.GetFiles(folder).Length == 0, "Cancelling a download removes the partial installer"); }
            }
            using (var updates = new AppUpdates(new Handler(_ => new(HttpStatusCode.Forbidden))))
            {
                try { await updates.CheckAsync(current, CancellationToken.None); throw new Exception("Rate limit was ignored"); }
                catch (HttpRequestException) { check(true, "A GitHub API failure is reported without offering an update"); }
            }
            var start = AppUpdates.InstallerStartInfo("setup.exe", Path.Combine(folder, "App & Frames"), Path.Combine(folder, "install log.txt"));
            check(start.UseShellExecute && start.ArgumentList.Contains("/UPDATE=1") && start.ArgumentList.Contains("/NORESTART")
                && start.ArgumentList.Contains($"/DIR={Path.Combine(folder, "App & Frames")}")
                && start.ArgumentList.Contains($"/LOG={Path.Combine(folder, "install log.txt")}"),
                "Update setup keeps paths with spaces and shell characters as individual arguments");
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    public static async Task RunLive(string folder)
    {
        using var updates = new AppUpdates();
        var release = await updates.CheckAsync(new Version(0, 0), CancellationToken.None) ?? throw new Exception("No stable release found");
        string file = await updates.DownloadAsync(release, folder, null, CancellationToken.None);
        Console.WriteLine($"PASS Official release {release.DisplayVersion}: downloaded {release.Size} bytes and verified SHA-256\n{file}");
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }

    private sealed class CancellingStream(CancellationTokenSource cancellation) : MemoryStream(Payload)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int count = await base.ReadAsync(buffer, cancellationToken);
            cancellation.Cancel();
            return count;
        }
    }
}
