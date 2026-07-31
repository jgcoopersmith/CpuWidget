using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
// WinForms is referenced for the tray icon; keep the WPF types as the unqualified ones.
using UserControl = System.Windows.Controls.UserControl;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;

namespace CpuWidget;

/// <summary>
/// One labelled device readout: load %, temperature, a history graph and a detail footer.
/// Used once for the CPU and once for the GPU.
/// </summary>
public partial class MetricPanel : UserControl
{
    private const int HistoryLength = 90;   // samples kept in the graph (~90 s at 1 Hz)
    private const double TempGraphMin = 25; // °C at the bottom of the graph
    private const double TempGraphMax = 100;

    private readonly List<DeviceReading> _history = new();
    private DeviceReading? _last;
    private double _graphWidth, _graphHeight;

    /// <summary>Display unit for every panel. Sensors and thresholds stay in Celsius.</summary>
    public static bool UseFahrenheit { get; set; }

    private static float ToDisplay(float celsius) => UseFahrenheit ? celsius * 9f / 5f + 32f : celsius;
    private static string UnitLabel => UseFahrenheit ? "°F" : "°C";

    public MetricPanel()
    {
        InitializeComponent();
    }

    /// <summary>Device label shown top-left, e.g. "CPU".</summary>
    public string Title
    {
        get => TitleText.Text;
        set => TitleText.Text = value;
    }

    /// <summary>Colour used for the load number and load graph.</summary>
    public Brush Accent
    {
        set
        {
            UsageText.Foreground = value;
            UsageUnitText.Foreground = value;
            UsageLine.Stroke = value;
            if (value is SolidColorBrush b)
            {
                var c = b.Color;
                UsageFill.Fill = new SolidColorBrush(Color.FromArgb(0x33, c.R, c.G, c.B));
            }
        }
    }

    public string DeviceName
    {
        get => DeviceNameText.Text;
        set => DeviceNameText.Text = value;
    }

    /// <summary>Message shown in the footer when there is no temperature to display.</summary>
    public string? StatusMessage { get; set; }

    /// <summary>Latest temperature, for the tray tooltip.</summary>
    public float? LastTemp { get; private set; }

    public void Update(DeviceReading r)
    {
        LastTemp = r.Temp;
        _last = r;

        _history.Add(r);
        if (_history.Count > HistoryLength) _history.RemoveRange(0, _history.Count - HistoryLength);

        Render(r);
    }

    /// <summary>Redraws the last sample — used when the temperature unit changes.</summary>
    public void Refresh()
    {
        if (_last is not null) Render(_last);
    }

    private void Render(DeviceReading r)
    {
        UsageText.Text = r.Load is float load ? load.ToString("0") : "--";

        TempUnitText.Text = UnitLabel;

        if (r.Temp is float temp)
        {
            TempText.Text = ToDisplay(temp).ToString("0");
            // Colour thresholds are defined in Celsius regardless of the display unit.
            var brush = new SolidColorBrush(TempColor(temp));
            TempText.Foreground = brush;
            TempUnitText.Foreground = brush;
            TempLine.Stroke = brush;
        }
        else
        {
            TempText.Text = "--";
        }

        ClockTextBlock.Text = r.ClockMhz is float mhz ? $"{mhz / 1000f:0.00} GHz" : "";

        // A missing temperature always has a reason — show it rather than a bare "--".
        if (r.Temp is null && StatusMessage is string status)
        {
            DetailText.Text = status;
        }
        else
        {
            var parts = new List<string>();
            if (r.SecondaryTemp is float second && r.Temp is float primary && Math.Abs(second - primary) >= 1)
                parts.Add($"{r.SecondaryLabel} {ToDisplay(second):0}°");
            if (r.MemoryUsedMb is float mb) parts.Add($"{mb / 1024f:0.0} GB");
            if (r.PowerWatts is float w) parts.Add($"{w:0} W");
            DetailText.Text = string.Join("   ", parts);
        }

        DrawGraph();
    }

    public static Color TempColor(float c) => c switch
    {
        >= 90 => Color.FromRgb(0xFF, 0x4D, 0x4D),
        >= 75 => Color.FromRgb(0xFF, 0x7A, 0x45),
        >= 60 => Color.FromRgb(0xFF, 0xB0, 0x4D),
        _ => Color.FromRgb(0x62, 0xD0, 0x95),
    };

    private void Graph_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        _graphWidth = e.NewSize.Width;
        _graphHeight = e.NewSize.Height;
        DrawGraph();
    }

    private void DrawGraph()
    {
        if (_graphWidth <= 1 || _graphHeight <= 1 || _history.Count < 2) return;

        // Newest sample sits at the right edge; the graph fills in from the right as history builds.
        double step = _graphWidth / (HistoryLength - 1);
        int first = HistoryLength - _history.Count;

        var usage = new PointCollection();
        var temps = new PointCollection();

        for (int i = 0; i < _history.Count; i++)
        {
            double x = (first + i) * step;

            if (_history[i].Load is float load)
                usage.Add(new Point(x, _graphHeight - (load / 100.0 * (_graphHeight - 2)) - 1));

            if (_history[i].Temp is float t)
            {
                double norm = Math.Clamp((t - TempGraphMin) / (TempGraphMax - TempGraphMin), 0, 1);
                temps.Add(new Point(x, _graphHeight - (norm * (_graphHeight - 2)) - 1));
            }
        }

        UsageLine.Points = usage;
        TempLine.Points = temps;

        if (usage.Count >= 2)
        {
            var fill = new PointCollection(usage)
            {
                new Point(usage[^1].X, _graphHeight),
                new Point(usage[0].X, _graphHeight),
            };
            UsageFill.Points = fill;
        }
    }
}
