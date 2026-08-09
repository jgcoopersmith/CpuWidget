using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;
// WinForms is referenced only for the tray icon; keep the WPF types as the unqualified ones.
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;
using Separator = System.Windows.Controls.Separator;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Point = System.Windows.Point;
using Matrix = System.Windows.Media.Matrix;

namespace CpuWidget;

public partial class MainWindow : Window
{
    private readonly HardwareMonitor _monitor = new();
    private readonly Settings _settings = Settings.Load();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };

    private Forms.NotifyIcon? _tray;
    private Drawing.Icon? _trayIcon;
    private MenuItem _startupItem = null!;   // built in BuildContextMenu, from the constructor
    private bool _reading;   // guards against overlapping sensor reads

    private const string TaskName = "CpuWidget";

    public MainWindow()
    {
        InitializeComponent();

        VersionText.Text = AppVersion.Display;

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

        if (_settings.Width is double w) Width = Math.Clamp(w, MinWidth, MaxWidth);

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

        _ = RefreshStartupItemAsync();
    }

    private async Task RefreshStartupItemAsync()
    {
        bool exists = await Task.Run(StartupTaskExists);
        _startupItem.IsChecked = exists;
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

            _lastCpu = cpu;
            UpdateTrayTooltip();
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

    // --- edge resizing ---------------------------------------------------

    private int _grip;              // -1 dragging the left edge, +1 the right, 0 idle
    private double _dragStartLeft, _dragStartWidth, _dragStartCursorX;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT p);

    /// <summary>
    /// Cursor X in device-independent units. Read from the screen rather than from mouse-event
    /// coordinates: those are relative to the window, which moves while the left edge is dragged.
    /// </summary>
    private double CursorX()
    {
        if (!GetCursorPos(out var p)) return _dragStartCursorX;
        var transform = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice;
        return transform is Matrix m ? m.Transform(new Point(p.X, p.Y)).X : p.X;
    }

    private void Grip_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var grip = (FrameworkElement)sender;
        _grip = (string)grip.Tag == "L" ? -1 : 1;
        _dragStartLeft = Left;
        _dragStartWidth = ActualWidth;
        _dragStartCursorX = CursorX();
        grip.CaptureMouse();
        e.Handled = true;   // don't let the window-drag handler start moving the widget
    }

    private void Grip_MouseMove(object sender, MouseEventArgs e)
    {
        if (_grip == 0) return;

        // If the button came up without us seeing it, stop resizing rather than tracking
        // the cursor with no button held.
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            EndResize(sender);
            return;
        }

        double delta = CursorX() - _dragStartCursorX;
        double width = Math.Clamp(_dragStartWidth + delta * _grip, MinWidth, MaxWidth);

        // Dragging the left edge keeps the right edge pinned.
        if (_grip < 0) Left = _dragStartLeft + (_dragStartWidth - width);
        Width = width;
    }

    private void Grip_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_grip == 0) return;
        EndResize(sender);
        e.Handled = true;
    }

    /// <summary>
    /// Also wired to LostMouseCapture: Windows can take capture away mid-drag (Alt+Tab, a
    /// system menu), and without this the widget would keep resizing on plain hover.
    /// </summary>
    private void Grip_LostMouseCapture(object sender, MouseEventArgs e) => EndResize(sender);

    private void EndResize(object sender)
    {
        if (_grip == 0) return;
        _grip = 0;
        if (sender is FrameworkElement grip && grip.IsMouseCaptured) grip.ReleaseMouseCapture();

        _settings.Width = Width;
        _settings.Left = Left;
        _settings.Save();
    }

    // --- tray icon -------------------------------------------------------

    private void SetupTray()
    {
        _trayIcon = MakeTrayIcon();
        _tray = new Forms.NotifyIcon
        {
            Visible = true,
            Text = "CPU Widget",
            Icon = _trayIcon,
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

    /// <summary>
    /// Renders the thermostat dial at the shell's small-icon size, so it stays sharp at
    /// whatever DPI the tray is running.
    /// </summary>
    private static Drawing.Icon MakeTrayIcon()
    {
        int size = Math.Max(16, Forms.SystemInformation.SmallIconSize.Width);
        using var bmp = ThermostatIcon.Render(size);

        IntPtr handle = bmp.GetHicon();
        try
        {
            // FromHandle doesn't own the handle, so clone into a managed icon before
            // destroying it — otherwise the tray shows a dead handle.
            using var temp = Drawing.Icon.FromHandle(handle);
            return (Drawing.Icon)temp.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    private DeviceReading? _lastCpu;

    private void UpdateTrayTooltip()
    {
        if (_tray is null || _lastCpu is null) return;

        string unit = MetricPanel.UseFahrenheit ? "°F" : "°C";
        static float Show(float c) => MetricPanel.UseFahrenheit ? c * 9f / 5f + 32f : c;

        // "--" rather than 0: an unknown load is not the same as an idle one.
        string tip = (_lastCpu.Load is float load ? $"CPU {load:0}%" : "CPU --") +
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

        // Whether the task exists is resolved off-thread in OnLoaded; querying schtasks.exe
        // synchronously here would stall window creation.
        _startupItem = new MenuItem { Header = "Start with Windows", IsCheckable = true };
        _startupItem.Click += (_, _) => SetStartupTask(_startupItem.IsChecked);
        menu.Items.Add(_startupItem);

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

    /// <summary>
    /// Checks against the whole virtual desktop, not the primary monitor's work area —
    /// otherwise a widget parked on a second monitor is dragged back to the primary on
    /// every launch.
    /// </summary>
    private void EnsureOnScreen()
    {
        double vLeft = SystemParameters.VirtualScreenLeft;
        double vTop = SystemParameters.VirtualScreenTop;
        double vRight = vLeft + SystemParameters.VirtualScreenWidth;
        double vBottom = vTop + SystemParameters.VirtualScreenHeight;

        // Height is measured by layout; fall back to a sane value if called before that.
        double height = ActualHeight > 0 ? ActualHeight : 160;

        // A sliver still on screen is fine; only fully (or nearly) lost windows get moved.
        const double margin = 40;
        bool offScreen = Left + Width < vLeft + margin || Left > vRight - margin
                      || Top + height < vTop + margin || Top > vBottom - margin;

        if (offScreen) ResetPosition();
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _timer.Stop();
        _settings.Left = Left;
        _settings.Top = Top;
        _settings.Width = Width;
        _settings.Save();

        if (_tray is not null)
        {
            _tray.Visible = false;
            _tray.Dispose();
        }
        _trayIcon?.Dispose();   // NotifyIcon doesn't own the icon it was handed
        _monitor.Dispose();
    }
}
