using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MuMuDeviceFrames;

/// <summary>MuMu's renderer RPC, kept connected while motion is in use. The manager CLI starts a process for every
/// sample (about 300 ms); this is the same local sensor operation, without that process or Android app installation.</summary>
internal sealed class MotionSensors : IMotionSensors
{
    private readonly IntPtr _stub, _procedure;
    private IntPtr _binding;
    public Vector3 Neutral { get; }

    public MotionSensors(MuMuInstance instance, uint pid)
    {
        using var process = Process.GetProcessById((int)pid);
        string shell = Path.GetDirectoryName(process.MainModule!.FileName)!;
        string engine = Path.GetDirectoryName(shell)!;
        string renderer = Path.Combine(engine, "device", "libRenderer.dll");
        string root = Path.GetDirectoryName(instance.ManagerPath)!;
        if (Path.GetFileName(root) is "nx_main" or "shell") root = Path.GetDirectoryName(root)!;
        string endpoint = FindEndpoint(Path.Combine(root, "vms"), instance.Index, Path.GetFileName(engine));
        (_stub, _procedure) = RendererRpc.Load(renderer);

        IntPtr text = IntPtr.Zero;
        try
        {
            Check(RpcStringBindingComposeA(null, "ncalrpc", null, endpoint, null, out text));
            Check(RpcBindingFromStringBindingA(text, out _binding));
            Check(RpcBindingSetOption(_binding, 12, 750)); // RPC_C_OPT_CALL_TIMEOUT, milliseconds
            string dump = instance.Shell("dumpsys sensorservice; settings get system user_rotation", timeoutMs: 3000).Output;
            Neutral = ParseAcceleration(dump) ?? MotionMath.DefaultGravity(ParseRotation(dump));
        }
        catch { Dispose(); throw; }
        finally { if (text != IntPtr.Zero) RpcStringFreeA(ref text); }
    }

    internal static string FindEndpoint(string vms, string index, string engine)
    {
        var paths = Directory.EnumerateDirectories(vms).Where(p => Path.GetFileName(p).EndsWith("-" + index, StringComparison.Ordinal))
            .OrderByDescending(p => Path.GetFileName(p).Contains("-" + engine + "-", StringComparison.Ordinal)).ToArray();
        foreach (string folder in paths)
        {
            string path = Path.Combine(folder, "configs", "vm_config.json");
            if (!File.Exists(path)) continue;
            using var json = JsonDocument.Parse(File.ReadAllText(path));
            if (json.RootElement.TryGetProperty("vm", out var vm) && vm.TryGetProperty("ginstance", out var value)
                && value.GetString() is { Length: > 0 } endpoint && Regex.IsMatch(endpoint, @"\Aginstance\d+\z")) return endpoint;
        }
        throw new InvalidOperationException("Could not find this emulator's motion sensor connection.");
    }

    internal static Vector3? ParseAcceleration(string dump)
    {
        var block = Regex.Match(dump, @"(?m)^3-axis Accelerometer: last \d+ events\r?\n(?<events>(?:[ \t]+[^\r\n]*\r?\n)+)");
        var readings = Regex.Matches(block.Groups["events"].Value,
            @"\)\s+(-?[\d.]+),\s+(-?[\d.]+),\s+(-?[\d.]+),");
        if (readings.Count == 0) return null;
        var last = readings[^1];
        if (!float.TryParse(last.Groups[1].Value, CultureInfo.InvariantCulture, out float x)
            || !float.TryParse(last.Groups[2].Value, CultureInfo.InvariantCulture, out float y)
            || !float.TryParse(last.Groups[3].Value, CultureInfo.InvariantCulture, out float z)) return null;
        var vector = new Vector3(x, y, z);
        return float.IsFinite(vector.LengthSquared()) && vector.LengthSquared() is > 1 and < 2500 ? vector : null;
    }

    private static int ParseRotation(string dump) => int.TryParse(dump.Trim().Split('\n')[^1], out int rotation) ? rotation : 0;

    public void SetAcceleration(Vector3 value)
    {
        if (_binding == IntPtr.Zero) throw new ObjectDisposedException(nameof(MotionSensors));
        byte[] name = Encoding.ASCII.GetBytes("acceleration_set_value"), parameters = Encoding.ASCII.GetBytes(Format(value));
        if (NdrClientCall2(_stub, _procedure, _binding, 0, name.Length, name, parameters.Length, parameters) == IntPtr.Zero)
            throw new InvalidOperationException("MuMu did not accept the motion sensor update.");
    }

    internal static string Format(Vector3 value) => FormattableString.Invariant($"{value.X:0.#####} {value.Y:0.#####} {value.Z:0.#####}");

    public void Dispose() { if (_binding != IntPtr.Zero) RpcBindingFree(ref _binding); }

    private static void Check(int status)
    {
        if (status != 0) throw new InvalidOperationException($"MuMu's motion sensor connection failed (RPC {status}).");
    }

    [DllImport("rpcrt4.dll", CharSet = CharSet.Ansi)] private static extern int RpcStringBindingComposeA(string? uuid, string protocol, string? network, string endpoint, string? options, out IntPtr text);
    [DllImport("rpcrt4.dll")] private static extern int RpcBindingFromStringBindingA(IntPtr text, out IntPtr binding);
    [DllImport("rpcrt4.dll")] private static extern int RpcStringFreeA(ref IntPtr text);
    [DllImport("rpcrt4.dll")] private static extern int RpcBindingFree(ref IntPtr binding);
    [DllImport("rpcrt4.dll")] private static extern int RpcBindingSetOption(IntPtr binding, uint option, nuint value);
    [DllImport("rpcrt4.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr NdrClientCall2(IntPtr stub, IntPtr procedure, IntPtr binding, int id, int nameLength, byte[] name, int parameterLength, byte[] parameters);
}

/// <summary>Finds and validates the vendor's own NDR descriptors from its PE metadata. No code addresses or offsets
/// into a particular MuMu build are used. Unsupported interfaces fail before making a sensor call.</summary>
internal static class RendererRpc
{
    private static readonly Dictionary<string, (IntPtr Stub, IntPtr Procedure)> Libraries = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Guid Interface = new("4F867F27-7292-4FE3-A424-CF166FCFC326");
    private static readonly Guid Ndr = new("8A885D04-1CEB-11C9-9FE8-08002B104860");

    internal static (IntPtr Stub, IntPtr Procedure) Load(string path)
    {
        lock (Libraries)
        {
            if (Libraries.TryGetValue(path, out var found)) return found;
            if (!Environment.Is64BitProcess) throw new NotSupportedException("Motion controls require a 64-bit build.");
            byte[] file = File.ReadAllBytes(path);
            int rva = FindInterface(file);
            if (rva < 0) throw new NotSupportedException("This MuMu renderer does not expose the supported motion sensor interface.");
            // Keep the library loaded: its allocator, type descriptors and procedure descriptors belong together.
            IntPtr library = LoadLibraryExW(path, IntPtr.Zero, 0x1100); // DLL_LOAD_DIR | DEFAULT_DIRS
            if (library == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Could not load MuMu's sensor interface.");
            try
            {
                IntPtr server = Marshal.ReadIntPtr(library + rva, 80);
                IntPtr stub = Marshal.ReadIntPtr(server), format = Marshal.ReadIntPtr(server, 16), offsets = Marshal.ReadIntPtr(server, 24);
                IntPtr procedure = format + (ushort)Marshal.ReadInt16(offsets, 25 * 2);
                byte[] bytes = new byte[66];
                Marshal.Copy(procedure, bytes, 0, bytes.Length);
                if (!IsSensorProcedure(bytes)) throw new NotSupportedException("MuMu's motion sensor interface has changed.");
                return Libraries[path] = (stub, procedure);
            }
            catch { FreeLibrary(library); throw; }
        }
    }

    internal static bool IsSensorProcedure(byte[] b) => b.Length >= 66 && b[0] == 0 && b[1] == 0x48
        && BitConverter.ToUInt16(b, 6) == 25 && BitConverter.ToUInt16(b, 8) == 56 && b[10] == 0x32 && b[19] == 6
        && Enumerable.Range(0, 6).All(i => BitConverter.ToUInt16(b, 32 + i * 6) == (i + 1) * 8)
        && new ushort[] { 0x48, 0x48, 0x0b, 0x48, 0x0b, 0x70 }.Select((flags, i) => BitConverter.ToUInt16(b, 30 + i * 6) == flags).All(x => x);

    internal static int FindInterface(byte[] file)
    {
        if (file.Length < 256 || BitConverter.ToUInt16(file, 0) != 0x5a4d) return -1;
        int pe = BitConverter.ToInt32(file, 0x3c);
        if (pe < 0 || pe > file.Length - 24 || BitConverter.ToUInt32(file, pe) != 0x4550) return -1;
        int sections = BitConverter.ToUInt16(file, pe + 6), optional = BitConverter.ToUInt16(file, pe + 20);
        int table = pe + 24 + optional;
        byte[] id = Interface.ToByteArray(), ndr = Ndr.ToByteArray();
        for (int i = 0; i < sections && table + i * 40 <= file.Length - 40; i++)
        {
            int section = table + i * 40, rva = BitConverter.ToInt32(file, section + 12);
            int length = BitConverter.ToInt32(file, section + 16), start = BitConverter.ToInt32(file, section + 20);
            if (start < 0 || length < 96 || start > file.Length - length) continue;
            for (int p = start; p <= start + length - 96; p += 4)
                if (BitConverter.ToInt32(file, p) == 96 && file.AsSpan(p + 4, 16).SequenceEqual(id)
                    && BitConverter.ToUInt32(file, p + 20) == 1 && file.AsSpan(p + 24, 16).SequenceEqual(ndr)
                    && BitConverter.ToUInt64(file, p + 48) != 0 && BitConverter.ToUInt64(file, p + 80) != 0)
                    return rva + p - start;
        }
        return -1;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr LoadLibraryExW(string file, IntPtr reserved, uint flags);
    [DllImport("kernel32.dll")] private static extern bool FreeLibrary(IntPtr library);
}

internal interface IMotionSensors : IDisposable
{
    Vector3 Neutral { get; }
    void SetAcceleration(Vector3 value);
}

/// <summary>One worker per emulator, with a single pending sample. Slow or disconnected sensors cannot queue a drag
/// history or block mouse input, and the final neutral sample always follows any update already in progress.</summary>
internal sealed class MotionSensorDriver(Func<IMotionSensors> connect, Action<Exception> failed) : IDisposable
{
    private readonly object _gate = new();
    private readonly AutoResetEvent _wake = new(false);
    private MotionSample? _pending;
    private Task? _worker;
    private bool _stopping;

    public void Submit(MotionSample sample)
    {
        lock (_gate)
        {
            if (_stopping || _worker?.IsCompleted == true) return;
            _pending = sample;
            _worker ??= Task.Run(Run);
            _wake.Set();
        }
    }

    private void Run()
    {
        IMotionSensors? sensors = null;
        bool changed = false;
        try
        {
            while (true)
            {
                _wake.WaitOne();
                lock (_gate) if (_stopping) break;
                MotionSample? sample;
                lock (_gate) { if (_stopping) break; sample = _pending; _pending = null; }
                if (sample == null) continue;
                if (sensors == null)
                {
                    if (sample == MotionSample.Neutral) continue;
                    sensors = connect();
                    // Connection setup reads Android once. Prefer any newer position, including a release, afterwards.
                    lock (_gate) { if (_stopping) break; if (_pending is { } newer) { sample = newer; _pending = null; } }
                }
                changed = true;
                sensors.SetAcceleration(MotionMath.Acceleration(sensors.Neutral, sample.Value));
                if (sample == MotionSample.Neutral)
                {
                    changed = false;
                    sensors.Dispose();
                    sensors = null; // re-read neutral after a pose/rotation change before the next gesture
                }
            }
        }
        catch (Exception ex) { failed(ex); }
        finally
        {
            try { if (changed) sensors?.SetAcceleration(sensors.Neutral); }
            catch (Exception ex) { Log.Write($"Motion sensor reset failed: {ex.Message}"); }
            sensors?.Dispose();
        }
    }

    public void Dispose()
    {
        Task? worker;
        lock (_gate) { if (_stopping) return; _stopping = true; _pending = null; _wake.Set(); worker = _worker; }
        // A bounded RPC call is the only work that can still be in progress. Always restore before the app exits.
        worker?.Wait(TimeSpan.FromSeconds(5));
        if (worker == null || worker.IsCompleted) _wake.Dispose();
    }
}
