using System.Numerics;

namespace MuMuDeviceFrames;

internal readonly record struct MotionSample(float Pitch, float Roll, Vector3 Shake, PointF Offset, float Wobble)
{
    public static MotionSample Neutral => default;
}

internal static class MotionMath
{
    public const float Gravity = 9.80665f, MaxTilt = MathF.PI / 10; // 18 degrees

    public static Vector3 DefaultGravity(int rotation) => rotation switch
    {
        1 => new(Gravity, 0, 0), 2 => new(0, -Gravity, 0), 3 => new(-Gravity, 0, 0), _ => new(0, Gravity, 0),
    };

    public static Vector3 Acceleration(Vector3 neutral, MotionSample sample)
    {
        Vector3 vertical = new(neutral.X, neutral.Y, 0);
        vertical = vertical.LengthSquared() < 0.01f ? Vector3.UnitY : Vector3.Normalize(vertical);
        Vector3 horizontal = new(vertical.Y, -vertical.X, 0);
        float x = Vector3.Dot(neutral, horizontal), y = Vector3.Dot(neutral, vertical), z = neutral.Z;
        float c = MathF.Cos(sample.Roll), s = MathF.Sin(sample.Roll);
        // The device's axes turn with the visual roll, so gravity moves the opposite way in those axes.
        (x, y) = (x * c - y * s, y * c + x * s);
        c = MathF.Cos(sample.Pitch); s = MathF.Sin(sample.Pitch);
        (y, z) = (y * c - z * s, y * s + z * c);
        return horizontal * (x + sample.Shake.X) + vertical * (y + sample.Shake.Y) + Vector3.UnitZ * (z + sample.Shake.Z);
    }

    /// <summary>One perspective quad for both bezel and screen; roll follows horizontal dragging, pitch vertical.</summary>
    public static PointF[] Project(Size image, float pitch, float roll, PointF centre)
    {
        float yaw = roll / 3, camera = Math.Max(image.Width, image.Height) * 3f;
        float cp = MathF.Cos(pitch), sp = MathF.Sin(pitch), cy = MathF.Cos(yaw), sy = MathF.Sin(yaw);
        float cr = MathF.Cos(roll), sr = MathF.Sin(roll);
        return new[] { (-1, -1), (1, -1), (1, 1), (-1, 1) }.Select(corner =>
        {
            float x = corner.Item1 * image.Width / 2f, y = corner.Item2 * image.Height / 2f;
            float z = y * sp; y *= cp;
            (x, z) = (x * cy + z * sy, z * cy - x * sy);
            (x, y) = (x * cr - y * sr, x * sr + y * cr);
            float scale = camera / (camera - z);
            return new PointF(centre.X + x * scale, centre.Y + y * scale);
        }).ToArray();
    }
}

/// <summary>Transient motion only. A tap adds energy to an existing shake; letting go eases the tilt back to neutral.</summary>
internal sealed class DeviceMotionState
{
    private Point _origin;
    private Size _size;
    private float _targetPitch, _targetRoll, _pitch, _roll, _energy;
    private double _last, _shakeStart;
    private double _lastTap = double.NegativeInfinity;
    private int _tapStrength;
    public bool Tilting { get; private set; }
    public int Strength => _energy < 0.02f ? 0 : Math.Clamp((int)Math.Ceiling(_energy), 1, 5);
    public bool Active => Tilting || _energy >= 0.02f || Math.Abs(_pitch) + Math.Abs(_roll) >= 0.001f;

    public void BeginTilt(Point cursor, Size size, double now)
    {
        _origin = cursor; _size = size; _last = now;
        Tilting = true;
    }

    public void MoveTilt(Point cursor)
    {
        if (!Tilting) return;
        _targetRoll = Math.Clamp((cursor.X - _origin.X) / Math.Max(80f, _size.Width / 2f), -1, 1) * MotionMath.MaxTilt;
        _targetPitch = Math.Clamp((cursor.Y - _origin.Y) / Math.Max(80f, _size.Height / 2f), -1, 1) * MotionMath.MaxTilt;
    }

    public void EndTilt() { Tilting = false; _targetPitch = _targetRoll = 0; }

    public void Shake(double now)
    {
        Advance(now);
        if (_energy < 0.02f) _shakeStart = now;
        _tapStrength = now - _lastTap <= 1.4 ? Math.Min(5, _tapStrength + 1) : 1;
        _lastTap = now;
        _energy = _tapStrength;
    }

    private void Advance(double now)
    {
        float dt = (float)Math.Clamp(now - _last, 0, 5);
        _last = now;
        _energy = _tapStrength * MathF.Exp(-(float)Math.Max(0, now - _lastTap - 0.9) * 2.5f);
        if (_energy < 0.02f) _energy = 0;
        float follow = 1 - MathF.Exp(-dt / 0.065f);
        _pitch += (_targetPitch - _pitch) * follow;
        _roll += (_targetRoll - _roll) * follow;
        if (!Tilting && Math.Abs(_pitch) + Math.Abs(_roll) < 0.001f) _pitch = _roll = 0;
    }

    public MotionSample Sample(double now)
    {
        Advance(now);
        float phase = (float)((now - _shakeStart) * Math.PI * 2 * 7);
        float wave = MathF.Cos(phase), cross = MathF.Sin(phase * 1.17f);
        var shake = new Vector3(wave, cross * 0.3f, cross * 0.55f) * (_energy * MotionMath.Gravity * 1.8f);
        return new MotionSample(_pitch, _roll, shake, new PointF(wave * _energy * 2.5f, cross * _energy), wave * _energy * MathF.PI / 240);
    }
}

internal sealed class DeviceMotionPreview : IDisposable
{
    private readonly OverlayWindow _window;
    private readonly Surface _surface;
    private readonly Point _position;

    public DeviceMotionPreview(IntPtr owner, Size size, Point position)
    {
        int margin = (int)Math.Ceiling(Math.Max(size.Width, size.Height) * 0.24) + 20;
        _surface = new Surface(size.Width + 2 * margin, size.Height + 2 * margin);
        _position = new Point(position.X - margin, position.Y - margin);
        _window = new OverlayWindow(owner, interactive: true) { Mouse = (_, _) => true };
    }

    public bool Visible => _window.IsVisible;

    public void Show(Texture image, MotionSample sample)
    {
        _surface.Clear();
        var centre = new PointF(_surface.Width / 2f + sample.Offset.X, _surface.Height / 2f + sample.Offset.Y);
        var quad = MotionMath.Project(new Size(image.Width, image.Height), sample.Pitch, sample.Roll + sample.Wobble, centre);
        _surface.Draw(quad, image, 0, 1, null, 0, 1, 1, 0, 1, 1);
        _window.SetBitmap(_surface.Pixels, _surface.Width, _surface.Height, _position);
        _window.SetVisible(true);
    }

    public void Dispose() { if (_window.IsAlive) _window.DestroyHandle(); _surface.Dispose(); }
}
