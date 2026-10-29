using System.Numerics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using MuMuDeviceFrames;

internal static class MotionTests
{
    public static void Render(string title, string folder)
    {
        Directory.CreateDirectory(folder);
        var instance = MuMuControl.LocateByName(title) ?? throw new Exception("Emulator not found");
        var config = AppConfig.Load();
        var library = FrameLibrary.Load(config.FramesRoot);
        var display = MuMuControl.QueryDisplay(instance);
        var pose = IPhoneDuo.Detect(display);
        var frame = library.Find(pose is { } p ? IPhoneDuo.FrameKey(p) : config.Instance(title).Frame) ?? library.Frames.First();
        using var original = FramedScreenshot.Compose(frame, instance.Screencap()!, null);
        using var bitmap = new Bitmap(760, (int)Math.Round(original.Height * 760.0 / original.Width), PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(bitmap)) g.DrawImage(original, new Rectangle(Point.Empty, bitmap.Size));
        var data = bitmap.LockBits(new Rectangle(Point.Empty, bitmap.Size), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
        var pixels = new byte[bitmap.Width * bitmap.Height * 4];
        for (int y = 0; y < bitmap.Height; y++) Marshal.Copy(data.Scan0 + y * data.Stride, pixels, y * bitmap.Width * 4, bitmap.Width * 4);
        bitmap.UnlockBits(data);
        using var surface = new Surface(1100, bitmap.Height + 300);
        var quad = MotionMath.Project(bitmap.Size, 0.22f, -0.15f, new(surface.Width / 2f, surface.Height / 2f));
        surface.Draw(quad, new Texture(pixels, bitmap.Width, bitmap.Height), 0, 1, null, 0, 1, 1, 0, 1, 1);
        surface.Save(Path.Combine(folder, "tilt.png"), 255);
        foreach (float scale in new[] { 1f, 1.5f })
        {
            var panel = DeviceMotionPanel.Preview(scale, 3, hover: true);
            using var output = new Bitmap(panel.Size.Width, panel.Size.Height, PixelFormat.Format32bppPArgb);
            data = output.LockBits(new Rectangle(Point.Empty, output.Size), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
            for (int y = 0; y < output.Height; y++) Marshal.Copy(panel.Pixels, y * output.Width * 4, data.Scan0 + y * data.Stride, output.Width * 4);
            output.UnlockBits(data);
            output.Save(Path.Combine(folder, $"shake-{scale:0.0}.png"));
        }
        Console.WriteLine($"Rendered motion controls into {Path.GetFullPath(folder)}");
    }

    public static void RunLive(string title)
    {
        var instance = MuMuControl.LocateByName(title) ?? throw new Exception("Emulator not found");
        using var process = System.Diagnostics.Process.GetProcessesByName("MuMuNxDevice").Single(p => p.MainWindowTitle == title);
        using var sensors = new MotionSensors(instance, (uint)process.Id);
        Console.WriteLine($"Neutral: {sensors.Neutral}");
        using (var capture = new NemuCapture(instance, (uint)process.Id))
        {
            double Brightness(NemuCapture.Framebuffer image)
            {
                long sum = 0;
                for (int p = 0; p < image.Pixels.Length; p += 4) sum += image.Pixels[p] + image.Pixels[p + 1] + image.Pixels[p + 2];
                return sum / (double)(image.Width * image.Height * 3);
            }
            double before = Brightness(capture.Capture());
            try
            {
                MuMuDeviceFrames.Native.GetWindowRect(process.MainWindowHandle, out var bounds);
                EmulatorClip.Hide(process.MainWindowHandle, new Point(bounds.Width / 2, bounds.Height / 2));
                double hidden = Brightness(capture.Capture());
                if (before < 20 || hidden < before * 0.8) throw new Exception($"Live screen blanked under the motion clip: {before:0.0} -> {hidden:0.0}");
                Console.WriteLine($"PASS Live framebuffer remains visible under the one-pixel motion clip: brightness {before:0.0} -> {hidden:0.0}");
            }
            finally { EmulatorClip.Remove(process.MainWindowHandle); }
        }
        void Expect(Vector3 expected, string description)
        {
            for (int i = 0; i < 8; i++)
            {
                var actual = MotionSensors.ParseAcceleration(instance.Shell("dumpsys sensorservice", timeoutMs: 3000).Output);
                if (actual is { } value && Vector3.Distance(value, expected) < 0.05f)
                {
                    Console.WriteLine($"PASS {description}: Android reports {value}");
                    return;
                }
                Thread.Sleep(60);
            }
            throw new Exception($"Android did not report {expected}: {description}");
        }
        try
        {
            var tilt = new MotionSample(0.2f, 0.15f, Vector3.Zero, default, 0);
            var expected = MotionMath.Acceleration(sensors.Neutral, tilt);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            sensors.SetAcceleration(expected);
            Console.WriteLine($"RPC update: {clock.Elapsed.TotalMilliseconds:0.00} ms");
            Expect(expected, "Tilt reaches the Android accelerometer");
            var state = new DeviceMotionState();
            state.Shake(0);
            expected = MotionMath.Acceleration(sensors.Neutral, state.Sample(0));
            sensors.SetAcceleration(expected);
            Expect(expected, "One shake tap");
            state.Shake(0);
            expected = MotionMath.Acceleration(sensors.Neutral, state.Sample(0));
            sensors.SetAcceleration(expected);
            Expect(expected, "Two shake taps increase measured acceleration");
        }
        finally
        {
            sensors.SetAcceleration(sensors.Neutral);
            Expect(sensors.Neutral, "All accelerometer axes restore to their original values");
        }
    }

    public static void Run(Action<bool, string> check)
    {
        foreach (int rotation in Enumerable.Range(0, 4))
        {
            Vector3 neutral = MotionMath.DefaultGravity(rotation);
            var pose = new MotionSample(0.2f, -0.25f, Vector3.Zero, default, 0);
            Vector3 tilted = MotionMath.Acceleration(neutral, pose);
            check(Math.Abs(tilted.Length() - neutral.Length()) < 0.0001f && Vector3.Distance(tilted, neutral) > 1,
                $"Tilt rotates gravity without adding acceleration, display rotation {rotation}");
            check(MotionMath.Acceleration(neutral, MotionSample.Neutral) == neutral, $"Neutral restores all axes, rotation {rotation}");
            Vector3 horizontal = Vector3.Normalize(new Vector3(neutral.Y, -neutral.X, 0));
            foreach (int direction in new[] { -1, 1 })
            {
                var drag = new DeviceMotionState();
                drag.BeginTilt(new(200, 400), new(400, 800), 0);
                drag.MoveTilt(new(200 + direction * 100, 400));
                var sample = drag.Sample(1);
                check(Math.Sign(sample.Roll) == direction && sample.Pitch == 0,
                    $"Horizontal drag keeps its visual direction and leaves pitch unchanged ({direction}, rotation {rotation})");
                check(Vector3.Dot(MotionMath.Acceleration(neutral, sample), horizontal) * direction < -1,
                    $"Accelerometer follows the device's horizontal tilt ({direction}, rotation {rotation})");
            }
        }
        var state = new DeviceMotionState();
        state.Shake(10);
        var first = state.Sample(10);
        state.Shake(10);
        var second = state.Sample(10);
        check(second.Shake.Length() > first.Shake.Length() * 1.9f, "Rapid shake taps increase actual sensor amplitude");
        state.Shake(10.2);
        check(state.Strength == 3, "Ordinary repeated taps build strength despite decay between clicks");
        for (int i = 0; i < 50; i++) state.Shake(10.2);
        check(state.Strength == 5 && float.IsFinite(state.Sample(10.2).Shake.Length()), "Shake strength is bounded at five");
        var paced = new DeviceMotionState();
        for (int i = 0; i < 5; i++) paced.Shake(i * 0.6);
        check(paced.Strength == 5 && paced.Sample(3).Shake.Length() > 1, "Five comfortably paced taps reach full strength and hold before fading");
        check(state.Sample(15) == MotionSample.Neutral && !state.Active, "Shake settles to exact neutral and stops");
        state.BeginTilt(new(100, 100), new(400, 800), 20);
        state.MoveTilt(new(10000, -10000));
        var extreme = state.Sample(21);
        check(Math.Abs(extreme.Pitch) <= MotionMath.MaxTilt && Math.Abs(extreme.Roll) <= MotionMath.MaxTilt, "Dragging outside the device clamps both tilt axes");
        state.EndTilt();
        check(state.Sample(25) == MotionSample.Neutral && !state.Active, "Releasing tilt returns to exact neutral");
        foreach (var size in new[] { new System.Drawing.Size(400, 900), new System.Drawing.Size(900, 400) })
        {
            var quad = MotionMath.Project(size, 0, 0, new(size.Width / 2f, size.Height / 2f));
            check(quad[0] == System.Drawing.PointF.Empty && quad[2] == new System.Drawing.PointF(size.Width, size.Height), "Neutral projection preserves the full device picture");
        }
        string dump = "3-axis Accelerometer: last 2 events\n\t1 (ts=1, wall=10:00:00.000) 0.10, 9.80, 0.20, \n\t2 (ts=2, wall=10:00:00.050) -2.50, 8.10, 4.30, \nActive sensors:\n";
        check(MotionSensors.ParseAcceleration(dump) == new Vector3(-2.5f, 8.1f, 4.3f), "Baseline uses the most recent hardware accelerometer event");
        check(MotionSensors.ParseAcceleration("Gyroscope Bias (debug): last 1 events\n\t1 (ts=1) 0, 0, 0,\n") == null, "Unrelated sensor events cannot become the gravity baseline");
        var tinyFrame = new RenderedFrame { Key = new RenderKey("test", 2, 2, 0), Width = 2, Height = 2,
            Pixels = new byte[16], RenderOffset = Point.Empty, ScreenRuns = [(0, 0, 2), (1, 0, 2)], Stretched = false };
        byte[] rgbaBottomUp = [0, 0, 255, 255, 255, 255, 0, 255, 255, 0, 0, 255, 0, 255, 0, 255];
        byte[] bgraTopDown = [0, 0, 255, 255, 0, 255, 0, 255, 255, 0, 0, 255, 0, 255, 255, 255];
        var composed = MotionCapture.Compose(tinyFrame, rgbaBottomUp, new Size(2, 2), new RectangleF(0, 0, 2, 2), true);
        check(composed.Pixels.SequenceEqual(bgraTopDown), "Live framebuffer keeps the correct colors and top-to-bottom orientation");
        check(MotionSensors.Format(new(1.25f, -9.80665f, 0)) == "1.25 -9.80665 0", "Sensor wire values use invariant decimal points");

        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var restored = new ManualResetEventSlim();
        var writes = new List<Vector3>();
        var fake = new FakeSensors(value =>
        {
            lock (writes) writes.Add(value);
            if (!entered.IsSet) { entered.Set(); release.Wait(TimeSpan.FromSeconds(3)); }
            if (value == new Vector3(0, MotionMath.Gravity, 0)) restored.Set();
        });
        var driver = new MotionSensorDriver(() => fake, ex => throw new Exception("Unexpected sensor failure", ex));
        driver.Submit(first);
        check(entered.Wait(TimeSpan.FromSeconds(3)), "Sensor updates run on their worker");
        for (int i = 0; i < 100; i++) driver.Submit(second);
        driver.Submit(MotionSample.Neutral);
        release.Set();
        check(restored.Wait(TimeSpan.FromSeconds(3)), "A release behind an in-flight sample always restores neutral");
        driver.Dispose(); driver.Dispose();
        lock (writes) check(writes.Count == 2 && writes[^1] == fake.Neutral, "Pending motion coalesces, without replaying stale drag positions");
    }

    private sealed class FakeSensors(Action<Vector3> write) : IMotionSensors
    {
        public Vector3 Neutral => new(0, MotionMath.Gravity, 0);
        public void SetAcceleration(Vector3 value) => write(value);
        public void Dispose() { }
    }
}
