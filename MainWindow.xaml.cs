using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;
// WinForms is referenced only for the tray icon; keep the WPF types as the unqualified ones.
using Color = System.Windows.Media.Color;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;
using Separator = System.Windows.Controls.Separator;

namespace CpuWidget;

public partial class MainWindow : Window
{
    private readonly HardwareMonitor _monitor = new();
    private readonly Settings _settings = Settings.Load();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };

    private Forms.NotifyIcon? _tray;
    private bool _reading;   // guards against overlapping sensor reads

    private const string TaskName = "CpuWidget";

    public MainWindow()
    {
        InitializeComponent();

        Topmost = _settings.AlwaysOnTop;
        Opacity = _settings.Opacity;
        MetricPanel.UseFahrenheit = _settings.Fahrenheit;

        BuildContextMenu();
        MouseLeftButtonDown += (_, _) => { if (Mouse.LeftButton == MouseButtonState.Pressed) DragMove(); };

        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _monitor.Start();

        CpuPanel.DeviceName = _monitor.CpuName;
        CpuPanel.StatusMessage = _monitor.Status;

        if (_monitor.GpuPresent)
        {
            GpuPanel.DeviceName = _monitor.GpuName;
            GpuPanel.StatusMessage = _monitor.Status;
        }
        else
        {
            // No discrete/integrated GPU exposed any sensors — drop the section entirely
            // rather than showing a row of dashes.
            GpuPanel.Visibility = Visibility.Collapsed;
            Divider.Visibility = Visibility.Collapsed;
        }

        // Position after layout so the measured height is known.
        if (_settings.Left is double l && _settings.Top is double t)
        {
            Left = l;
            Top = t;
        }
        else
        {
            ResetPosition();
        }
        EnsureOnScreen();

        SetupTray();

        _timer.Tick += (_, _) => Tick();
        _timer.Start();
        Tick();
    }

    private async void Tick()
    {
        if (_reading) return;
        _reading = true;
        try
        {
            // Sensor polling touches the driver and can block for a few ms — keep it off the UI thread.
            var (cpu, gpu) = await Task.Run(_monitor.Read);

            CpuPanel.StatusMessage = _monitor.Status;
            CpuPanel.Update(cpu);

            if (_monitor.GpuPresent)
            {
                GpuPanel.StatusMessage = _monitor.Status;
                GpuPanel.Update(gpu);
            }

            UpdateTrayIcon(cpu);
        }
        catch (Exception ex)
        {
            HardwareMonitor.Log($"Tick FAILED: {ex}");
        }
        finally
        {
            _reading = false;
        }
    }

    // --- tray icon -------------------------------------------------------

    private void SetupTray()
    {
        _tray = new Forms.NotifyIcon
        {
            Visible = true,
            Text = "CPU Widget",
            Icon = Drawing.SystemIcons.Application,
        };
        _tray.MouseClick += (_, e) =>
        {
            if (e.Button != Forms.MouseButtons.Left) return;
            if (IsVisible) Hide();
            else { Show(); Activate(); }
        };

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Show / hide", null, (_, _) => { if (IsVisible) Hide(); else Show(); });
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => Close());
        _tray.ContextMenuStrip = menu;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);

    private void UpdateTrayIcon(DeviceReading cpu)
    {
        if (_tray is null) return;

        int pct = (int)Math.Round(cpu.Load ?? 0);
        using var bmp = new Drawing.Bitmap(16, 16);
        using (var g = Drawing.Graphics.FromImage(bmp))
        {
            g.Clear(Drawing.Color.Transparent);
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.SingleBitPerPixelGridFit;

            // A filled bar behind the number gives an at-a-glance read even at 16px.
            int barHeight = (int)Math.Round(pct / 100.0 * 16);
            var c = cpu.Temp is float tv ? MetricPanel.TempColor(tv) : Color.FromRgb(0x5A, 0xA9, 0xFF);
            using var bar = new Drawing.SolidBrush(Drawing.Color.FromArgb(120, c.R, c.G, c.B));
            g.FillRectangle(bar, 0, 16 - barHeight, 16, barHeight);

            string text = pct >= 100 ? "99" : pct.ToString();
            using var font = new Drawing.Font("Segoe UI", 7.5f, Drawing.FontStyle.Bold, Drawing.GraphicsUnit.Point);
            using var white = new Drawing.SolidBrush(Drawing.Color.White);
            var size = g.MeasureString(text, font);
            g.DrawString(text, font, white, (16 - size.Width) / 2, (16 - size.Height) / 2);
        }

        IntPtr h = bmp.GetHicon();
        try
        {
            var old = _tray.Icon;
            _tray.Icon = Drawing.Icon.FromHandle(h);
            old?.Dispose();
        }
        finally
        {
            DestroyIcon(h);
        }

        _lastCpu = cpu;
        UpdateTrayTooltip();
    }

    private DeviceReading? _lastCpu;

    private void UpdateTrayTooltip()
    {
        if (_tray is null || _lastCpu is null) return;

        string unit = MetricPanel.UseFahrenheit ? "°F" : "°C";
        static float Show(float c) => MetricPanel.UseFahrenheit ? c * 9f / 5f + 32f : c;

        string tip = $"CPU {_lastCpu.Load ?? 0:0}%" +
                     (_lastCpu.Temp is float t ? $"  {Show(t):0}{unit}" : "");
        if (_monitor.GpuPresent && GpuPanel.LastTemp is float gt)
            tip += $"\nGPU {Show(gt):0}{unit}";
        _tray.Text = tip;
    }

    // --- context menu ----------------------------------------------------

    private void BuildContextMenu()
    {
        var menu = new ContextMenu();

        var onTop = new MenuItem { Header = "Always on top", IsCheckable = true, IsChecked = _settings.AlwaysOnTop };
        onTop.Click += (_, _) =>
        {
            _settings.AlwaysOnTop = onTop.IsChecked;
            Topmost = onTop.IsChecked;
            _settings.Save();
        };
        menu.Items.Add(onTop);

        var fahrenheit = new MenuItem
        {
            Header = "Show °F",
            IsCheckable = true,
            IsChecked = _settings.Fahrenheit,
        };
        fahrenheit.Click += (_, _) =>
        {
            _settings.Fahrenheit = fahrenheit.IsChecked;
            MetricPanel.UseFahrenheit = fahrenheit.IsChecked;
            _settings.Save();

            // Redraw immediately rather than waiting for the next poll.
            CpuPanel.Refresh();
            GpuPanel.Refresh();
            UpdateTrayTooltip();
        };
        menu.Items.Add(fahrenheit);

        var opacity = new MenuItem { Header = "Opacity" };
        foreach (var value in new[] { 1.0, 0.92, 0.8, 0.65 })
        {
            var item = new MenuItem { Header = $"{value * 100:0}%" };
            item.Click += (_, _) => { Opacity = _settings.Opacity = value; _settings.Save(); };
            opacity.Items.Add(item);
        }
        menu.Items.Add(opacity);

        var startup = new MenuItem { Header = "Start with Windows", IsCheckable = true, IsChecked = StartupTaskExists() };
        startup.Click += (_, _) => SetStartupTask(startup.IsChecked);
        menu.Items.Add(startup);

        menu.Items.Add(new Separator());

        var reset = new MenuItem { Header = "Reset position" };
        reset.Click += (_, _) => ResetPosition();
        menu.Items.Add(reset);

        var exit = new MenuItem { Header = "Exit" };
        exit.Click += (_, _) => Close();
        menu.Items.Add(exit);

        ContextMenu = menu;
    }

    // Elevated apps can't autostart from the Run key without a UAC prompt every boot,
    // so use a scheduled task registered to run with highest privileges instead.
    private static bool StartupTaskExists() => RunSchtasks($"/Query /TN \"{TaskName}\"") == 0;

    private void SetStartupTask(bool enable)
    {
        string exe = Environment.ProcessPath ?? "";
        int code = enable
            ? RunSchtasks($"/Create /TN \"{TaskName}\" /TR \"\\\"{exe}\\\"\" /SC ONLOGON /RL HIGHEST /F")
            : RunSchtasks($"/Delete /TN \"{TaskName}\" /F");

        if (code != 0) HardwareMonitor.Log($"schtasks failed, exit={code}");
    }

    private static int RunSchtasks(string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("schtasks.exe", args)
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (p is null) return -1;
            p.WaitForExit(10_000);
            return p.ExitCode;
        }
        catch
        {
            return -1;
        }
    }

    // --- lifecycle -------------------------------------------------------

    private void ResetPosition()
    {
        var wa = SystemParameters.WorkArea;
        Left = wa.Right - Width - 18;
        Top = wa.Top + 18;
    }

    private void EnsureOnScreen()
    {
        var wa = SystemParameters.WorkArea;
        if (Left + Width < wa.Left + 40 || Left > wa.Right - 40 || Top < wa.Top - 10 || Top > wa.Bottom - 40)
            ResetPosition();
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _timer.Stop();
        _settings.Left = Left;
        _settings.Top = Top;
        _settings.Save();

        if (_tray is not null)
        {
            _tray.Visible = false;
            _tray.Dispose();
        }
        _monitor.Dispose();
    }
}
