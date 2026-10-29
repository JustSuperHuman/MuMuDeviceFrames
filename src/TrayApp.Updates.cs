using System.Diagnostics;

namespace MuMuDeviceFrames;

internal sealed partial class TrayApp
{
    private readonly CancellationTokenSource _updateLifetime = new();
    private System.Windows.Forms.Timer? _updateTimer;
    private CancellationTokenSource? _updateDownload;
    private AppUpdate? _availableUpdate;
    private bool _updateBusy, _installingUpdate, _updateNotification;
    private int _updatePercent;

    private void InitializeUpdates()
    {
        AppUpdates.CleanOldDownloads();
        _tray.BalloonTipClicked += (_, _) =>
        {
            if (_updateNotification && !_updateBusy)
            {
                _updateNotification = false;
                OfferAppUpdate();
            }
        };
        // Give the emulators time to attach before checking, then check the schedule every hour.
        _updateTimer = new System.Windows.Forms.Timer { Interval = 10_000 };
        _updateTimer.Tick += (_, _) =>
        {
            _updateTimer.Interval = 3_600_000;
            CheckForAppUpdates(manual: false);
        };
        _updateTimer.Start();
    }

    private MenuItem AppUpdateItem()
    {
        string text = _installingUpdate ? "Installing app update…"
            : _updateDownload != null ? $"Downloading app update… {_updatePercent}%"
            : _updateBusy ? "Checking for app updates…"
            : _availableUpdate != null ? $"Install app update {_availableUpdate.DisplayVersion}…"
            : "Check for app updates…";
        return new MenuItem(text, DownloadGlyph, () =>
        {
            if (_availableUpdate != null) OfferAppUpdate();
            else CheckForAppUpdates(manual: true);
        }) { Enabled = !_updateBusy, Hint = $"v{AppUpdates.DisplayVersion(AppUpdates.CurrentVersion)}" };
    }

    private async void CheckForAppUpdates(bool manual)
    {
        if (_updateBusy || _updateLifetime.IsCancellationRequested) return;
        if (!manual && (!Config.AutomaticUpdateChecks || Config.LastUpdateCheckUtc is { } last
                && last <= DateTime.UtcNow && DateTime.UtcNow - last < TimeSpan.FromDays(1))) return;
        _updateBusy = true;
        try
        {
            using var updates = new AppUpdates();
            _availableUpdate = await updates.CheckAsync(AppUpdates.CurrentVersion, _updateLifetime.Token);
            if (_updateLifetime.IsCancellationRequested) return;
            Config.LastUpdateCheckUtc = DateTime.UtcNow;
            Config.Save();
            if (_availableUpdate is { } available)
            {
                if (manual) OfferAppUpdate();
                else if (Config.LastNotifiedUpdate != available.Tag)
                {
                    Notify($"Version {available.DisplayVersion} is available. Click to download and install, or use the tray menu.");
                    _updateNotification = true;
                    Config.LastNotifiedUpdate = available.Tag;
                    Config.Save();
                }
            }
            else if (manual)
                MessageBox.Show($"You're up to date.\n\nMuMu Device Frames {AppUpdates.DisplayVersion(AppUpdates.CurrentVersion)} is the latest stable version.",
                    "App updates", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (OperationCanceledException) when (_updateLifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (_updateLifetime.IsCancellationRequested) return;
            Log.Write($"Update check failed: {ex.Message}");
            if (manual) MessageBox.Show($"Could not check for app updates. Please try again later.\n\n{UpdateError(ex)}",
                "App updates", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally { _updateBusy = _updateDownload != null; }
    }

    private void OfferAppUpdate()
    {
        if (_availableUpdate is not { } update || _updateDownload != null || _updateLifetime.IsCancellationRequested) return;
        var answer = MessageBox.Show(
            $"MuMu Device Frames {update.DisplayVersion} is available. You're using {AppUpdates.DisplayVersion(AppUpdates.CurrentVersion)}.\n\n" +
            $"Download and install the update ({update.Size / 1024.0 / 1024:0.#} MB)?\n\n" +
            "The app will restart. Your frames, settings and Start with Windows choice will be kept.",
            "Update MuMu Device Frames", MessageBoxButtons.YesNo, MessageBoxIcon.Information, MessageBoxDefaultButton.Button2);
        if (answer == DialogResult.Yes) InstallAppUpdate(update);
    }

    private async void InstallAppUpdate(AppUpdate update)
    {
        if (_updateDownload != null || _updateLifetime.IsCancellationRequested) return;
        _updateDownload = CancellationTokenSource.CreateLinkedTokenSource(_updateLifetime.Token);
        _updateBusy = true;
        _updatePercent = 0;
        string? installer = null;
        bool installerStarted = false;
        try
        {
            Notify($"Downloading version {update.DisplayVersion}. You can cancel from Settings in the tray menu.");
            using var updates = new AppUpdates();
            var progress = new Progress<int>(percent => _updatePercent = percent);
            installer = await updates.DownloadAsync(update, AppUpdates.StagingDirectory, progress, _updateDownload.Token);
            _updateDownload.Token.ThrowIfCancellationRequested();
            Directory.CreateDirectory(AppConfig.Dir);
            // Setup asks this instance to exit only when it is ready to replace files, then launches the new version.
            using var process = Process.Start(AppUpdates.InstallerStartInfo(installer, AppContext.BaseDirectory,
                Path.Combine(AppConfig.Dir, "update-install.log"))) ?? throw new IOException("The update installer could not be started.");
            installerStarted = true;
            _installingUpdate = true;
            await process.WaitForExitAsync(_updateLifetime.Token);
            if (!_updateLifetime.IsCancellationRequested)
            {
                string reason = process.ExitCode == 0 ? "Setup finished without restarting the app. Please restart it manually."
                    : $"Setup did not finish (exit code {process.ExitCode}). You can retry from the tray menu.";
                Notify(reason, warning: true);
            }
        }
        catch (OperationCanceledException) when (_updateDownload.IsCancellationRequested)
        {
            if (!_updateLifetime.IsCancellationRequested) Notify("App update download cancelled.");
        }
        catch (Exception ex)
        {
            if (_updateLifetime.IsCancellationRequested) return;
            Log.Write($"Update installation failed: {ex}");
            MessageBox.Show($"Could not install the app update. You can retry from the tray menu.\n\n{UpdateError(ex)}",
                "App updates", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            if (installer != null && !installerStarted)
                try { File.Delete(installer); } catch (Exception ex) { Log.Write($"Update cleanup failed: {ex.Message}"); }
            _updateDownload.Dispose();
            _updateDownload = null;
            _updateBusy = _installingUpdate = false;
        }
    }

    private static string UpdateError(Exception error) => error is OperationCanceledException
        ? "The request timed out. Check your connection and try again." : error.Message;

    private void StopUpdates()
    {
        _updateTimer?.Stop();
        _updateTimer?.Dispose();
        _updateTimer = null;
        _updateLifetime.Cancel();
    }
}
