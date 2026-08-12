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
using Media = System.Windows.Media;

namespace CpuWidget;

public partial class MainWindow : Window
{
    private readonly HardwareMonitor _monitor = new();
    private readonly Settings _settings = Settings.Load();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };

    private Forms.NotifyIcon? _tray;
    private Drawing.Icon? _trayIcon;
    private MenuItem _startupItem = null!;   // built in BuildContextMenu, from the constructor
    private bool _startupEnabled;            // survives menu rebuilds
    private bool _reading;   // guards against overlapping sensor reads

    private const string TaskName = "CpuWidget";

    public MainWindow()
    {
        InitializeComponent();

        VersionText.Text = AppVersion.Display;

        Topmost = _settings.AlwaysOnTop;
        Opacity = _settings.Opacity;
        MetricPanel.UseFahrenheit = _settings.Fahrenheit;

        ApplyColors();
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
        _startupEnabled = await Task.Run(StartupTaskExists);
        _startupItem.IsChecked = _startupEnabled;
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

    // Windows 11 draws its own 1px frame around a window with a sizing border, squared off
    // at the corners and light grey in the light theme. The widget paints its own rounded
    // card, so that frame only ever shows as grey nicks at the four corners.
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWA_BORDER_COLOR = 34;
    private const int DWMWCP_DONOTROUND = 1;
    private const int DWMWA_COLOR_NONE = unchecked((int)0xFFFFFFFE);

    private static void SuppressSystemFrame(IntPtr hwnd)
    {
        try
        {
            int none = DWMWA_COLOR_NONE;
            DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref none, sizeof(int));

            // Don't let DWM round it either — the card's own corners are the ones that show.
            int square = DWMWCP_DONOTROUND;
            DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref square, sizeof(int));
        }
        catch (EntryPointNotFoundException)
        {
            // Older Windows: no such attributes, and no frame to suppress.
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (PresentationSource.FromVisual(this) is HwndSource source)
        {
            source.AddHook(WndProc);
            SuppressSystemFrame(source.Handle);
        }
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

    // --- colours ----------------------------------------------------------

    private const string DefaultTitleColor = "#FFD700";
    private const string DefaultCpuAccent = "#5AA9FF";
    private const string DefaultGpuAccent = "#A78BFA";
    private const string DefaultBackground = "#EE0E1116";
    private const string DefaultTempCool = "#62D095";
    private const string DefaultTempWarm = "#FFB04D";
    private const string DefaultTempHot = "#FF7A45";
    private const string DefaultTempCritical = "#FF4D4D";

    /// <summary>Whole-ramp presets, applied to all four temperature bands at once.</summary>
    private static readonly (string Name, string Cool, string Warm, string Hot, string Critical)[] RampPresets =
    {
        ("Classic", DefaultTempCool, DefaultTempWarm, DefaultTempHot, DefaultTempCritical),
        ("Fire", "#FFE066", "#FFB020", "#FF6B2C", "#E52020"),
        ("Ice", "#7FE3F0", "#45C8DE", "#3D8FD6", "#3A4FCF"),
        ("Neon", "#39FF88", "#C9FF3D", "#FF9F1C", "#FF2E88"),
        ("Mono", "#9AA6B4", "#C3CCD8", "#E4EAF1", "#FFFFFF"),
    };

    private static readonly (string Name, string Hex)[] Palette =
    {
        ("Gold", "#FFD700"),
        ("Amber", "#FFB020"),
        ("Red", "#FF5252"),
        ("Green", "#62D095"),
        ("Cyan", "#45C8DE"),
        ("Blue", "#5AA9FF"),
        ("Violet", "#A78BFA"),
        ("Pink", "#FF7AB6"),
        ("White", "#FFFFFF"),
        ("Grey", "#8FA0B4"),
    };

    // Backgrounds keep their alpha so the widget stays slightly translucent.
    private static readonly (string Name, string Hex)[] BackgroundPalette =
    {
        ("Charcoal", "#EE0E1116"),
        ("Slate", "#EE181D26"),
        ("Navy", "#EE0B1428"),
        ("Plum", "#EE180E22"),
        ("Forest", "#EE0C1C14"),
        ("Black", "#F0000000"),
    };

    private static Media.SolidColorBrush MakeBrush(string hex) =>
        new((Media.Color)Media.ColorConverter.ConvertFromString(hex)!);

    private static Media.Color ParseColor(string hex) =>
        (Media.Color)Media.ColorConverter.ConvertFromString(hex)!;

    private void ApplyColors()
    {
        TitleBar.Foreground = MakeBrush(_settings.TitleColor ?? DefaultTitleColor);
        CpuPanel.Accent = MakeBrush(_settings.CpuAccent ?? DefaultCpuAccent);
        GpuPanel.Accent = MakeBrush(_settings.GpuAccent ?? DefaultGpuAccent);
        RootBorder.Background = MakeBrush(_settings.Background ?? DefaultBackground);

        MetricPanel.CoolColor = ParseColor(_settings.TempCool ?? DefaultTempCool);
        MetricPanel.WarmColor = ParseColor(_settings.TempWarm ?? DefaultTempWarm);
        MetricPanel.HotColor = ParseColor(_settings.TempHot ?? DefaultTempHot);
        MetricPanel.CriticalColor = ParseColor(_settings.TempCritical ?? DefaultTempCritical);

        // Repaint the temperature figures and their graph lines with the new ramp.
        CpuPanel.Refresh();
        GpuPanel.Refresh();
    }

    /// <summary>Threshold label in whatever unit is on display.</summary>
    private static string Deg(float celsius) => MetricPanel.UseFahrenheit
        ? $"{celsius * 9 / 5 + 32:0}°F"
        : $"{celsius:0}°C";

    private MenuItem BuildColorMenu(string header, (string, string)[] palette,
                                    Func<string> get, Action<string> set)
    {
        var menu = new MenuItem { Header = header };

        foreach (var (name, hex) in palette)
        {
            var choice = new MenuItem { Header = name, Icon = Swatch(hex) };
            choice.Click += (_, _) => Choose(set, hex);
            menu.Items.Add(choice);
        }

        menu.Items.Add(new Separator());

        var custom = new MenuItem { Header = "Custom…", Icon = Swatch(get()) };
        custom.Click += (_, _) => PickCustomColor(get, set);
        menu.Items.Add(custom);

        return menu;
    }

    private static System.Windows.Shapes.Rectangle Swatch(string hex) => new()
    {
        Width = 12,
        Height = 12,
        Fill = MakeBrush(hex),
        Stroke = MakeBrush("#59FFFFFF"),
        StrokeThickness = 1,
        RadiusX = 2,
        RadiusY = 2,
    };

    /// <summary>A four-band strip previewing a whole ramp preset.</summary>
    private static System.Windows.Controls.StackPanel RampSwatch(
        (string Name, string Cool, string Warm, string Hot, string Critical) preset)
    {
        var strip = new System.Windows.Controls.StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
        };
        foreach (var hex in new[] { preset.Cool, preset.Warm, preset.Hot, preset.Critical })
        {
            strip.Children.Add(new System.Windows.Shapes.Rectangle
            {
                Width = 4,
                Height = 12,
                Fill = MakeBrush(hex),
            });
        }
        return strip;
    }

    private void Choose(Action<string> set, string hex)
    {
        set(hex);
        _settings.Save();
        ApplyColors();
        BuildContextMenu();   // refresh the "Custom…" swatches
    }

    /// <summary>Opens the system colour picker, keeping the current alpha.</summary>
    private void PickCustomColor(Func<string> get, Action<string> set)
    {
        var current = (Media.Color)Media.ColorConverter.ConvertFromString(get())!;

        using var dialog = new Forms.ColorDialog
        {
            FullOpen = true,
            Color = Drawing.Color.FromArgb(current.R, current.G, current.B),
        };

        if (dialog.ShowDialog() != Forms.DialogResult.OK) return;

        var picked = dialog.Color;
        Choose(set, $"#{current.A:X2}{picked.R:X2}{picked.G:X2}{picked.B:X2}");
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

        var colors = new MenuItem { Header = "Colors" };
        colors.Items.Add(BuildColorMenu("Title", Palette,
            () => _settings.TitleColor ?? DefaultTitleColor, v => _settings.TitleColor = v));
        colors.Items.Add(BuildColorMenu("CPU accent", Palette,
            () => _settings.CpuAccent ?? DefaultCpuAccent, v => _settings.CpuAccent = v));
        colors.Items.Add(BuildColorMenu("GPU accent", Palette,
            () => _settings.GpuAccent ?? DefaultGpuAccent, v => _settings.GpuAccent = v));
        colors.Items.Add(BuildColorMenu("Background", BackgroundPalette,
            () => _settings.Background ?? DefaultBackground, v => _settings.Background = v));

        // The temperature figures change colour with the reading, so the whole ramp is
        // configurable — as a set of presets, or band by band.
        var ramp = new MenuItem { Header = "Temperature scale" };

        var presets = new MenuItem { Header = "Preset" };
        foreach (var preset in RampPresets)
        {
            var item = new MenuItem { Header = preset.Name, Icon = RampSwatch(preset) };
            item.Click += (_, _) =>
            {
                _settings.TempCool = preset.Cool;
                _settings.TempWarm = preset.Warm;
                _settings.TempHot = preset.Hot;
                _settings.TempCritical = preset.Critical;
                _settings.Save();
                ApplyColors();
                BuildContextMenu();
            };
            presets.Items.Add(item);
        }
        ramp.Items.Add(presets);
        ramp.Items.Add(new Separator());

        ramp.Items.Add(BuildColorMenu($"Cool (below {Deg(MetricPanel.WarmAt)})", Palette,
            () => _settings.TempCool ?? DefaultTempCool, v => _settings.TempCool = v));
        ramp.Items.Add(BuildColorMenu($"Warm ({Deg(MetricPanel.WarmAt)}+)", Palette,
            () => _settings.TempWarm ?? DefaultTempWarm, v => _settings.TempWarm = v));
        ramp.Items.Add(BuildColorMenu($"Hot ({Deg(MetricPanel.HotAt)}+)", Palette,
            () => _settings.TempHot ?? DefaultTempHot, v => _settings.TempHot = v));
        ramp.Items.Add(BuildColorMenu($"Critical ({Deg(MetricPanel.CriticalAt)}+)", Palette,
            () => _settings.TempCritical ?? DefaultTempCritical, v => _settings.TempCritical = v));

        colors.Items.Add(ramp);

        colors.Items.Add(new Separator());
        var resetColors = new MenuItem { Header = "Reset colors" };
        resetColors.Click += (_, _) =>
        {
            _settings.TitleColor = null;
            _settings.CpuAccent = null;
            _settings.GpuAccent = null;
            _settings.Background = null;
            _settings.TempCool = null;
            _settings.TempWarm = null;
            _settings.TempHot = null;
            _settings.TempCritical = null;
            _settings.Save();
            ApplyColors();
            BuildContextMenu();
        };
        colors.Items.Add(resetColors);
        menu.Items.Add(colors);

        // Whether the task exists is resolved off-thread in OnLoaded; querying schtasks.exe
        // synchronously here would stall window creation. Rebuilding the menu (after a colour
        // change) must not forget what that lookup found, hence the cached flag.
        _startupItem = new MenuItem
        {
            Header = "Start with Windows",
            IsCheckable = true,
            IsChecked = _startupEnabled,
        };
        _startupItem.Click += (_, _) =>
        {
            _startupEnabled = _startupItem.IsChecked;
            SetStartupTask(_startupItem.IsChecked);
        };
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
