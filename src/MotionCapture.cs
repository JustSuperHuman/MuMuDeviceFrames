using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace MuMuDeviceFrames;

/// <summary>Captures the guest framebuffer, not the clipped Windows render window. MuMu can return black from
/// PrintWindow once its window is clipped down for a motion preview; its SDK continues to return live app pixels.</summary>
internal sealed class MotionCapture : IDisposable
{
    private volatile bool _stop;
    private Texture? _latest;
    public Texture? Latest => Volatile.Read(ref _latest);

    public MotionCapture(MuMuInstance instance, uint pid, RenderedFrame frame, Size renderSize, Rectangle picture, Action<Exception> failed)
    {
        _ = Task.Run(() =>
        {
            NemuCapture? sdk = null;
            try
            {
                try { sdk = new NemuCapture(instance, pid); }
                catch (Exception ex) { Log.Write($"Motion framebuffer SDK unavailable, using Android screenshots: {ex.Message}"); }
                while (!_stop)
                {
                    var clock = Stopwatch.StartNew();
                    Texture image;
                    if (sdk != null)
                    {
                        var shot = sdk.Capture();
                        var crop = new RectangleF(picture.X * shot.Width / (float)renderSize.Width,
                            picture.Y * shot.Height / (float)renderSize.Height, picture.Width * shot.Width / (float)renderSize.Width,
                            picture.Height * shot.Height / (float)renderSize.Height);
                        image = Compose(frame, shot.Pixels, new Size(shot.Width, shot.Height), crop, bottomUpRgba: true);
                    }
                    else
                    {
                        byte[] png = instance.Screencap() ?? throw new IOException("Could not capture the emulator's live screen.");
                        using var bitmap = new Bitmap(new MemoryStream(png));
                        var data = bitmap.LockBits(new Rectangle(Point.Empty, bitmap.Size), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                        var pixels = new byte[bitmap.Width * bitmap.Height * 4];
                        try { for (int y = 0; y < bitmap.Height; y++) Marshal.Copy(data.Scan0 + y * data.Stride, pixels, y * bitmap.Width * 4, bitmap.Width * 4); }
                        finally { bitmap.UnlockBits(data); }
                        image = Compose(frame, pixels, bitmap.Size, new RectangleF(PointF.Empty, bitmap.Size), bottomUpRgba: false);
                    }
                    if (_stop) break;
                    Volatile.Write(ref _latest, image);
                    int delay = Math.Max(1, (sdk != null ? 50 : 150) - (int)clock.ElapsedMilliseconds);
                    Thread.Sleep(delay);
                }
            }
            catch (Exception ex) { if (!_stop) failed(ex); }
            finally { sdk?.Dispose(); }
        });
    }

    /// <summary>Scales the picture into the same screen mask as the real emulator, then adds bezel and glass.</summary>
    internal static Texture Compose(RenderedFrame frame, byte[] source, Size size, RectangleF crop, bool bottomUpRgba)
    {
        var output = new byte[frame.Pixels.Length];
        float sx = crop.Width / frame.Key.RenderWidth, sy = crop.Height / frame.Key.RenderHeight;
        Parallel.ForEach(frame.ScreenRuns, run =>
        {
            float fy = Math.Clamp(crop.Y + (run.Y + 0.5f) * sy - 0.5f, 0, size.Height - 1);
            int y0 = (int)fy, y1 = Math.Min(y0 + 1, size.Height - 1);
            float ay = fy - y0;
            if (bottomUpRgba) { y0 = size.Height - 1 - y0; y1 = size.Height - 1 - y1; }
            for (int x = run.X0; x < run.X1; x++)
            {
                int dx = frame.RenderOffset.X + x, dy = frame.RenderOffset.Y + run.Y;
                if (dx < 0 || dy < 0 || dx >= frame.Width || dy >= frame.Height) continue;
                float fx = Math.Clamp(crop.X + (x + 0.5f) * sx - 0.5f, 0, size.Width - 1);
                int x0 = (int)fx, x1 = Math.Min(x0 + 1, size.Width - 1), d = (dy * frame.Width + dx) * 4;
                float ax = fx - x0;
                int a = (y0 * size.Width + x0) * 4, b = (y0 * size.Width + x1) * 4,
                    c = (y1 * size.Width + x0) * 4, e = (y1 * size.Width + x1) * 4;
                for (int channel = 0; channel < 3; channel++)
                {
                    int s = bottomUpRgba ? 2 - channel : channel;
                    float top = source[a + s] * (1 - ax) + source[b + s] * ax;
                    float bottom = source[c + s] * (1 - ax) + source[e + s] * ax;
                    output[d + channel] = (byte)(top * (1 - ay) + bottom * ay);
                }
                output[d + 3] = 255;
            }
        });
        if (frame.Glass is { } glass)
            for (int y = 0; y < glass.Bounds.Height; y++)
                for (int x = 0; x < glass.Bounds.Width; x++)
                    Blend(output, ((y + glass.Bounds.Y) * frame.Width + x + glass.Bounds.X) * 4, glass.Pixels, (y * glass.Bounds.Width + x) * 4);
        for (int p = 0; p < output.Length; p += 4) Blend(output, p, frame.Pixels, p);
        return new Texture(output, frame.Width, frame.Height);
    }

    private static void Blend(byte[] output, int d, byte[] layer, int s)
    {
        int a = layer[s + 3];
        if (a == 0) return;
        for (int c = 0; c < 4; c++) output[d + c] = (byte)(layer[s + c] + output[d + c] * (255 - a) / 255);
    }

    public void Dispose() => _stop = true;
}

internal sealed class NemuCapture : IDisposable
{
    internal sealed record Framebuffer(byte[] Pixels, int Width, int Height); // bottom-up RGBA, before conversion
    [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Unicode)] private delegate int Connect(string root, int index);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void Disconnect(int handle);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int CaptureDisplay(int handle, uint display, int length, out int width, out int height, IntPtr pixels);
    private static readonly Dictionary<string, (Connect Connect, Disconnect Disconnect, CaptureDisplay Capture)> Apis = new(StringComparer.OrdinalIgnoreCase);
    private readonly Disconnect _disconnect;
    private readonly CaptureDisplay _capture;
    private int _handle;
    private byte[] _pixels = [];

    public NemuCapture(MuMuInstance instance, uint pid)
    {
        using var process = Process.GetProcessById((int)pid);
        string shell = Path.GetDirectoryName(process.MainModule!.FileName)!;
        string path = Path.Combine(shell, "sdk", "external_renderer_ipc.dll");
        // Load once per engine, retaining the module while delegates or capture workers can still use it.
        (Connect Connect, Disconnect Disconnect, CaptureDisplay Capture) api;
        lock (Apis)
        {
            if (!Apis.TryGetValue(path, out api))
            {
                IntPtr library = NativeLibrary.Load(path);
                try
                {
                    api = (Marshal.GetDelegateForFunctionPointer<Connect>(NativeLibrary.GetExport(library, "nemu_connect")),
                        Marshal.GetDelegateForFunctionPointer<Disconnect>(NativeLibrary.GetExport(library, "nemu_disconnect")),
                        Marshal.GetDelegateForFunctionPointer<CaptureDisplay>(NativeLibrary.GetExport(library, "nemu_capture_display")));
                    Apis[path] = api;
                }
                catch { NativeLibrary.Free(library); throw; }
            }
        }
        _disconnect = api.Disconnect;
        _capture = api.Capture;
        string root = Path.GetDirectoryName(instance.ManagerPath)!;
        if (Path.GetFileName(root) is "nx_main" or "shell") root = Path.GetDirectoryName(root)!;
        _handle = api.Connect(root, int.Parse(instance.Index));
        if (_handle == 0) throw new IOException("MuMu's framebuffer connection failed.");
    }

    public unsafe Framebuffer Capture()
    {
        if (_capture(_handle, 0, 0, out int width, out int height, IntPtr.Zero) != 0 || width <= 0 || height <= 0
            || width > 10000 || height > 10000) throw new IOException("MuMu could not read the framebuffer size.");
        int length = checked(width * height * 4);
        if (_pixels.Length != length) _pixels = new byte[length];
        fixed (byte* pixels = _pixels)
            if (_capture(_handle, 0, length, out int actualWidth, out int actualHeight, (IntPtr)pixels) != 0
                || width != actualWidth || height != actualHeight) throw new IOException("MuMu's framebuffer changed during capture.");
        return new Framebuffer(_pixels, width, height); // private worker buffer; Compose consumes it before the next capture
    }

    public void Dispose() { if (_handle != 0) _disconnect(_handle); _handle = 0; }
}
