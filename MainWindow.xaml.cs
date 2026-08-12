using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
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
            GpuRow.Height = new GridLength(0);   // a starred row would still reserve space
            MinHeight = 52;
            Height = Math.Max(MinHeight, Height / 2);
        }

        if (_settings.Width is double w) Width = Math.Clamp(w, MinWidth, MaxWidth);
        if (_settings.Height is double h) Height = Math.Clamp(h, MinHeight, MaxHeight);

        ApplyScale();
        SizeChanged += (_, _) => ApplyScale();

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

    // --- scaling ----------------------------------------------------------

    // The size everything is drawn at 1:1. Shrinking below this scales the text down
    // rather than letting fixed font sizes set a floor on how small the widget can get.
    private const double DesignWidth = 300;
    private const double DesignPanelHeight = 131;   // one device section at 1:1
    private const double MinScale = 0.28;

    private void ApplyScale()
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) return;

        double panels = _monitor.GpuPresent ? 2 : 1;
        // + title bar, divider and version line
        double designHeight = DesignPanelHeight * panels + 43;

        // Uniform: whichever axis is tighter decides, so text never outgrows its box.
        double scale = Math.Clamp(
            Math.Min(ActualWidth / DesignWidth, ActualHeight / designHeight), MinScale, 1.0);

        ContentGrid.Margin = new Thickness(14 * scale, 9 * scale, 14 * scale, 8 * scale);
        RootBorder.CornerRadius = new CornerRadius(14 * scale);

        TitleBar.FontSize = Math.Max(5, 11 * scale);
        TitleBar.Margin = new Thickness(0, 0, 0, 4 * scale);
        TitleBar.Visibility = scale >= MetricPanel.DetailThreshold
            ? Visibility.Visible
            : Visibility.Collapsed;

        Divider.Margin = new Thickness(0, 9 * scale, 0, 8 * scale);
        VersionText.FontSize = Math.Max(4, 8 * scale);
        VersionText.Margin = new Thickness(0, 3 * scale, 0, -1);
        VersionText.Visibility = scale >= MetricPanel.DetailThreshold
            ? Visibility.Visible
            : Visibility.Collapsed;

        CpuPanel.ApplyScale(scale);
        GpuPanel.ApplyScale(scale);
    }

    // --- resizing from every edge and corner ------------------------------

    // The window is borderless, so Windows has no frame to hit-test. Reporting the border
    // codes ourselves hands resizing back to the OS, which brings corners, the proper
    // cursors and Aero snap along with it.
    private const int WM_NCHITTEST = 0x0084;
    private const int WM_EXITSIZEMOVE = 0x0232;
    private const int WM_GETMINMAXINFO = 0x0024;

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public System.Drawing.Point Reserved, MaxSize, MaxPosition, MinTrackSize, MaxTrackSize;
    }

    private const int HTCLIENT = 1, HTLEFT = 10, HTRIGHT = 11, HTTOP = 12, HTTOPLEFT = 13,
                      HTTOPRIGHT = 14, HTBOTTOM = 15, HTBOTTOMLEFT = 16, HTBOTTOMRIGHT = 17;

    /// <summary>Thickness of the grab band along each edge, in device-independent units.</summary>
    private const double GripBand = 7;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (PresentationSource.FromVisual(this) is HwndSource source)
            source.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case WM_NCHITTEST:
                int hit = HitTest(lParam);
                if (hit != HTCLIENT)
                {
                    handled = true;
                    return new IntPtr(hit);
                }
                break;

            case WM_GETMINMAXINFO:
                // Windows applies these limits in device pixels, so on a 200% display the
                // XAML values would let the widget shrink to half the intended floor.
                var info = Marshal.PtrToStructure<MinMaxInfo>(lParam);
                var toDevice = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice;
                double sx = toDevice?.M11 ?? 1.0, sy = toDevice?.M22 ?? 1.0;
                info.MinTrackSize = new System.Drawing.Point(
                    (int)Math.Ceiling(MinWidth * sx), (int)Math.Ceiling(MinHeight * sy));
                info.MaxTrackSize = new System.Drawing.Point(
                    (int)Math.Floor(MaxWidth * sx), (int)Math.Floor(MaxHeight * sy));
                Marshal.StructureToPtr(info, lParam, true);
                handled = true;
                return IntPtr.Zero;

            case WM_EXITSIZEMOVE:
                // Fired once when a drag or resize finishes, rather than on every pixel.
                _settings.Left = Left;
                _settings.Top = Top;
                _settings.Width = Width;
                _settings.Height = Height;
                _settings.Save();
                break;
        }
        return IntPtr.Zero;
    }

    private int HitTest(IntPtr lParam)
    {
        // lParam packs signed screen coordinates; they must stay signed for monitors
        // positioned left of or above the primary one.
        long raw = lParam.ToInt64();
        var screen = new Point((short)(raw & 0xFFFF), (short)((raw >> 16) & 0xFFFF));

        Point p;
        try { p = PointFromScreen(screen); }
        catch { return HTCLIENT; }   // no source yet

        bool left = p.X <= GripBand;
        bool right = p.X >= ActualWidth - GripBand;
        bool top = p.Y <= GripBand;
        bool bottom = p.Y >= ActualHeight - GripBand;

        if (top && left) return HTTOPLEFT;
        if (top && right) return HTTOPRIGHT;
        if (bottom && left) return HTBOTTOMLEFT;
        if (bottom && right) return HTBOTTOMRIGHT;
        if (left) return HTLEFT;
        if (right) return HTRIGHT;
        if (top) return HTTOP;
        if (bottom) return HTBOTTOM;
        return HTCLIENT;
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
        _settings.Height = Height;
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
