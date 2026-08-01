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
    private const double PixelsPerSample = 3;   // widening the widget buys more history, not wider pixels
    private const int MinSamples = 40;
    private const int MaxSamples = 900;
    private const double TempGraphMin = 25;     // °C at the bottom of the graph
    private const double TempGraphMax = 100;

    /// <summary>Samples the graph can show at the current width (~1 s each).</summary>
    private int Capacity => _graphWidth <= 1
        ? MinSamples
        : Math.Clamp((int)(_graphWidth / PixelsPerSample), MinSamples, MaxSamples);

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
        Trim();

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
        // The reading carries its own reason; StatusMessage is only a startup-time fallback.
        if (r.Temp is null && (r.Status ?? StatusMessage) is string status)
        {
            DetailText.Text = status;
        }
        else
        {
            var parts = new List<string>();
            // Compare what will actually be printed: a 1°C gap can round to the same °F,
            // which would show the secondary temperature as a duplicate of the primary.
            if (r.SecondaryTemp is float second && r.Temp is float primary &&
                Math.Round(ToDisplay(second)) != Math.Round(ToDisplay(primary)))
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

    private void Trim()
    {
        int capacity = Capacity;
        if (_history.Count > capacity) _history.RemoveRange(0, _history.Count - capacity);
    }

    private void Graph_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        _graphWidth = e.NewSize.Width;
        _graphHeight = e.NewSize.Height;
        Trim();          // narrowing drops the oldest samples that no longer fit
        DrawGraph();
    }

    private void DrawGraph()
    {
        if (_graphWidth <= 1 || _graphHeight <= 1 || _history.Count < 2) return;

        // Newest sample sits at the right edge; the graph fills in from the right as history builds.
        int capacity = Capacity;
        double step = _graphWidth / (capacity - 1);
        int first = capacity - _history.Count;

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
