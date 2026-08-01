using System.Runtime.InteropServices;
using LibreHardwareMonitor.Hardware;

namespace CpuWidget;

/// <summary>One sample from a device. Any field may be null when the sensor is unavailable.</summary>
public sealed class DeviceReading
{
    public float? Load;           // 0-100
    public float? Temp;           // primary temperature, °C (CPU package / GPU core)
    public float? SecondaryTemp;  // hottest core (CPU) or hot spot (GPU), °C
    public string SecondaryLabel = "core max";
    public float? ClockMhz;
    public float? PowerWatts;
    public float? MemoryUsedMb;   // GPU only
    /// <summary>Why a value is missing, if it is. Recomputed every poll, so it never goes stale.</summary>
    public string? Status;
}

/// <summary>
/// Reads CPU and GPU sensors through LibreHardwareMonitor. CPU temperature needs the
/// elevated kernel driver; GPU sensors generally work unelevated via the vendor APIs.
/// </summary>
public sealed class HardwareMonitor : IDisposable
{
    private sealed class UpdateVisitor : IVisitor
    {
        public void VisitComputer(IComputer computer) => computer.Traverse(this);
        public void VisitHardware(IHardware hardware)
        {
            hardware.Update();
            foreach (var sub in hardware.SubHardware) sub.Accept(this);
        }
        public void VisitSensor(ISensor sensor) { }
        public void VisitParameter(IParameter parameter) { }
    }

    private Computer? _computer;
    private readonly UpdateVisitor _visitor = new();
    private bool _dumpedSensors;

    /// <summary>
    /// Guards the LibreHardwareMonitor handle. Polling runs on a thread-pool thread while
    /// Dispose runs on the UI thread, and closing the driver mid-poll can fault natively.
    /// </summary>
    private readonly object _sync = new();

    public string CpuName { get; private set; } = "CPU";
    public string GpuName { get; private set; } = "";
    public bool GpuPresent { get; private set; }
    /// <summary>Null when the sensor library opened cleanly, otherwise why it didn't.</summary>
    public string? Status { get; private set; }

    private static readonly bool Elevated = ComputeElevated();

    private static string LogPath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "CpuWidget", "log.txt");

    private const long MaxLogBytes = 256 * 1024;
    private const int LogTailBytes = 64 * 1024;

    /// <summary>Appends a diagnostic line; never throws, never blocks the UI on failure.</summary>
    public static void Log(string message)
    {
        try
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(LogPath)!);

            // Keep the log bounded: past a quarter megabyte, drop all but the recent tail.
            var info = new System.IO.FileInfo(LogPath);
            if (info.Exists && info.Length > MaxLogBytes)
            {
                string existing = System.IO.File.ReadAllText(LogPath);
                string tail = existing.Length > LogTailBytes ? existing[^LogTailBytes..] : existing;
                System.IO.File.WriteAllText(LogPath, "--- log truncated ---" + Environment.NewLine + tail);
            }

            System.IO.File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss} {message}{Environment.NewLine}");
        }
        catch { /* logging must never take the widget down */ }
    }

    public void Start()
    {
        Log($"--- start, elevated={Elevated}, exe={Environment.ProcessPath}");
        try
        {
            _computer = new Computer { IsCpuEnabled = true, IsGpuEnabled = true };
            _computer.Open();
            _computer.Accept(_visitor);

            foreach (var hw in _computer.Hardware)
            {
                if (hw.HardwareType == HardwareType.Cpu)
                {
                    CpuName = ShortenName(hw.Name);
                }
                else if (IsGpu(hw.HardwareType) && !GpuPresent)
                {
                    GpuPresent = true;
                    GpuName = ShortenName(hw.Name);
                }
            }
            Log($"opened OK, cpu={CpuName}, gpu={(GpuPresent ? GpuName : "<none>")}");
        }
        catch (Exception ex)
        {
            _computer = null;
            Status = Elevated ? "sensor driver failed to load" : "run as Administrator for temperatures";
            Log($"Open() FAILED: {ex}");
        }
    }

    /// <summary>
    /// Explains a missing temperature. LibreHardwareMonitor does not throw when its kernel
    /// driver can't load — it opens normally and simply exposes no temperature sensors — so
    /// an unelevated run has to be detected here rather than from an exception in Start().
    /// </summary>
    private static string MissingTempReason(string device) =>
        Elevated ? $"no {device} temperature sensor" : "run as Administrator for temperatures";

    private static bool IsGpu(HardwareType t) =>
        t is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel;

    /// <summary>Polls every sensor once and returns the CPU and GPU samples together.</summary>
    public (DeviceReading Cpu, DeviceReading Gpu) Read()
    {
        var cpu = new DeviceReading { SecondaryLabel = "core max" };
        var gpu = new DeviceReading { SecondaryLabel = "hot spot" };

        string? readError = null;

        lock (_sync)
        {
            if (_computer is not null)
            {
                try
                {
                    _computer.Accept(_visitor);

                    foreach (var hw in _computer.Hardware)
                    {
                        if (hw.HardwareType == HardwareType.Cpu) ReadCpu(hw, cpu);
                        else if (IsGpu(hw.HardwareType)) ReadGpu(hw, gpu);
                    }

                    if (!_dumpedSensors)
                    {
                        _dumpedSensors = true;
                        foreach (var hw in _computer.Hardware)
                        {
                            if (hw.HardwareType != HardwareType.Cpu && !IsGpu(hw.HardwareType)) continue;
                            Log($"  [{hw.HardwareType}] {hw.Name}");
                            foreach (var s in hw.Sensors)
                                Log($"    {s.SensorType,-12} {s.Name,-30} = {(s.Value.HasValue ? s.Value.Value.ToString("0.##") : "<null>")}");
                        }
                    }

                    // Some parts expose only per-core temps, others only a package temp.
                    cpu.Temp ??= cpu.SecondaryTemp;
                    cpu.SecondaryTemp ??= cpu.Temp;
                    gpu.Temp ??= gpu.SecondaryTemp;
                    gpu.SecondaryTemp ??= gpu.Temp;
                }
                catch (Exception ex)
                {
                    // Don't kill the widget over a transient read failure, but never fail
                    // silently either — a swallowed exception here reads as "no temperature".
                    // Kept per-poll rather than sticky, so one blip doesn't poison the footer.
                    readError = ex.GetType().Name + ": " + ex.Message;
                    Log($"Read() FAILED: {ex}");
                }
            }
        }

        // LHM's "CPU Total" load sensor reads 0 on some systems even under real load,
        // so total CPU usage always comes from the kernel tick counters instead.
        cpu.Load = ReadLoadFallback();

        cpu.Status = Status ?? readError ?? (cpu.Temp is null ? MissingTempReason("CPU") : null);
        gpu.Status = Status ?? readError ?? (GpuPresent && gpu.Temp is null ? MissingTempReason("GPU") : null);

        return (cpu, gpu);
    }

    private static void ReadCpu(IHardware hw, DeviceReading r)
    {
        foreach (var s in hw.Sensors)
        {
            if (s.Value is not float v) continue;

            switch (s.SensorType)
            {
                case SensorType.Temperature:
                    // "Distance to TjMax" is a margin, not a temperature.
                    if (s.Name.Contains("Distance", StringComparison.OrdinalIgnoreCase)) break;
                    if (s.Name.Contains("Package", StringComparison.OrdinalIgnoreCase))
                        r.Temp = v;
                    else if (s.Name.Contains("Average", StringComparison.OrdinalIgnoreCase))
                        break; // an average, not a peak
                    else if (s.Name.Contains("Core", StringComparison.OrdinalIgnoreCase))
                        r.SecondaryTemp = Math.Max(r.SecondaryTemp ?? float.MinValue, v);
                    break;

                case SensorType.Clock:
                    // "Bus Speed" is also a Clock sensor — only core clocks are wanted.
                    if (s.Name.Contains("Core", StringComparison.OrdinalIgnoreCase))
                        r.ClockMhz = Math.Max(r.ClockMhz ?? float.MinValue, v);
                    break;

                case SensorType.Power:
                    if (s.Name.Contains("Package", StringComparison.OrdinalIgnoreCase))
                        r.PowerWatts = v;
                    break;
            }
        }
    }

    private static void ReadGpu(IHardware hw, DeviceReading r)
    {
        // Vendors name these differently, so prefer the canonical "GPU Core" sensor and
        // fall back to the first plausible one of each kind.
        foreach (var s in hw.Sensors)
        {
            if (s.Value is not float v) continue;
            bool isCore = s.Name.Contains("Core", StringComparison.OrdinalIgnoreCase);

            switch (s.SensorType)
            {
                case SensorType.Temperature:
                    if (s.Name.Contains("Hot Spot", StringComparison.OrdinalIgnoreCase) ||
                        s.Name.Contains("Hotspot", StringComparison.OrdinalIgnoreCase))
                        r.SecondaryTemp = v;
                    else if (isCore || r.Temp is null)
                        r.Temp = isCore ? v : r.Temp ?? v;
                    break;

                case SensorType.Load:
                    // "D3D 3D" is the fallback on Intel and on GPUs without a core-load sensor.
                    if (isCore) r.Load = v;
                    else if (r.Load is null && s.Name.Contains("D3D 3D", StringComparison.OrdinalIgnoreCase))
                        r.Load = v;
                    break;

                case SensorType.Clock:
                    if (isCore) r.ClockMhz = v;
                    break;

                case SensorType.Power:
                    if (s.Name.Contains("Package", StringComparison.OrdinalIgnoreCase) ||
                        s.Name.Equals("GPU Power", StringComparison.OrdinalIgnoreCase))
                        r.PowerWatts = v;
                    break;

                case SensorType.SmallData: // GPU memory is reported in MB as SmallData
                    if (s.Name.Contains("Memory Used", StringComparison.OrdinalIgnoreCase) &&
                        !s.Name.Contains("Dedicated", StringComparison.OrdinalIgnoreCase) &&
                        !s.Name.Contains("Shared", StringComparison.OrdinalIgnoreCase))
                        r.MemoryUsedMb = v;
                    else if (r.MemoryUsedMb is null &&
                             s.Name.Contains("D3D Dedicated Memory Used", StringComparison.OrdinalIgnoreCase))
                        r.MemoryUsedMb = v;
                    break;
            }
        }
    }

    // --- Total CPU load without any driver, from kernel/user/idle tick deltas. ---

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out long idle, out long kernel, out long user);

    private long _prevIdle, _prevKernel, _prevUser;
    private bool _havePrev;

    private float? ReadLoadFallback()
    {
        if (!GetSystemTimes(out long idle, out long kernel, out long user)) return null;

        float? result = null;
        if (_havePrev)
        {
            long idleDelta = idle - _prevIdle;
            long totalDelta = (kernel - _prevKernel) + (user - _prevUser); // kernel includes idle
            if (totalDelta > 0)
                result = Math.Clamp(100f * (totalDelta - idleDelta) / totalDelta, 0f, 100f);
        }

        _prevIdle = idle;
        _prevKernel = kernel;
        _prevUser = user;
        _havePrev = true;
        return result;
    }

    private static bool ComputeElevated()
    {
        using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
        return new System.Security.Principal.WindowsPrincipal(id)
            .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }

    private static string ShortenName(string name) => name
        .Replace("(R)", "").Replace("(TM)", "").Replace("CPU ", "")
        .Replace("NVIDIA ", "").Replace("GeForce ", "")
        .Replace("  ", " ").Trim();

    public void Dispose()
    {
        // Waits for an in-flight poll rather than pulling the driver out from under it.
        lock (_sync)
        {
            try { _computer?.Close(); } catch { /* driver already unloaded */ }
            _computer = null;
        }
    }
}
