using LibreHardwareMonitor.Hardware;

// Dumps every CPU sensor LibreHardwareMonitor can see, plus the driver state, so we can
// tell "no temperature sensors" apart from "driver refused to load".
// Everything printed is also teed to sensordump.txt next to the exe.

string logPath = Path.Combine(AppContext.BaseDirectory, "sensordump.txt");
var logWriter = new StreamWriter(logPath, append: false) { AutoFlush = true };
Console.SetOut(new TeeWriter(Console.Out, logWriter));

Console.WriteLine("=== SensorDump ===");
Console.WriteLine($"OS            : {Environment.OSVersion}");
Console.WriteLine($"Runtime       : {Environment.Version}");
Console.WriteLine($"Elevated      : {IsElevated()}");
Console.WriteLine($"Process path  : {Environment.ProcessPath}");
Console.WriteLine();

var computer = new Computer { IsCpuEnabled = true, IsMotherboardEnabled = true };

try
{
    computer.Open();
    Console.WriteLine("computer.Open() OK");
}
catch (Exception ex)
{
    Console.WriteLine($"computer.Open() FAILED: {ex}");
}

var visitor = new UpdateVisitor();
try
{
    computer.Accept(visitor);
    Thread.Sleep(1000);
    computer.Accept(visitor); // second pass: some sensors need a delta
}
catch (Exception ex)
{
    Console.WriteLine($"update FAILED: {ex}");
}

Console.WriteLine();
foreach (var hw in computer.Hardware)
{
    Dump(hw, 0);
}

Console.WriteLine();
Console.WriteLine("=== Ring0 driver service state ===");
foreach (var name in new[] { "WinRing0_1_2_0", "WinRing0x64", "LibreHardwareMonitor", "R0LibreHardwareMonitor", "PawnIOLHM", "PawnIO" })
{
    using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{name}");
    Console.WriteLine(key is null
        ? $"  {name,-24} : not registered"
        : $"  {name,-24} : registered, ImagePath={key.GetValue("ImagePath")}");
}

Console.WriteLine();
Console.WriteLine("=== Files next to the exe ===");
foreach (var f in Directory.GetFiles(Path.GetDirectoryName(Environment.ProcessPath!)!))
    Console.WriteLine("  " + Path.GetFileName(f));

Console.WriteLine();
Console.WriteLine($"Log written to: {logPath}");
Console.WriteLine("Press Enter to close.");
Console.ReadLine();
computer.Close();

static void Dump(IHardware hw, int indent)
{
    string pad = new(' ', indent * 2);
    Console.WriteLine($"{pad}[{hw.HardwareType}] {hw.Name}");
    foreach (var s in hw.Sensors.OrderBy(s => s.SensorType).ThenBy(s => s.Name))
        Console.WriteLine($"{pad}  {s.SensorType,-12} {s.Name,-28} = {(s.Value.HasValue ? s.Value.Value.ToString("0.##") : "<null>")}");
    foreach (var sub in hw.SubHardware)
        Dump(sub, indent + 1);
}

static bool IsElevated()
{
    using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
    return new System.Security.Principal.WindowsPrincipal(id)
        .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
}

sealed class UpdateVisitor : IVisitor
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

/// <summary>Writes to the console and the log file at once.</summary>
sealed class TeeWriter(TextWriter a, TextWriter b) : TextWriter
{
    public override System.Text.Encoding Encoding => a.Encoding;
    public override void Write(char value) { a.Write(value); b.Write(value); }
    public override void Write(string? value) { a.Write(value); b.Write(value); }
    public override void WriteLine(string? value) { a.WriteLine(value); b.WriteLine(value); }
}
