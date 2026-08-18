using System.Globalization;
using System.IO;
using System.Text;

namespace CpuWidget;

/// <summary>
/// Rolling record of performance, kept for a month.
///
/// Readings arrive once a second, which would be 2.6 million rows per device over 30 days,
/// so samples are folded into one-minute buckets holding the average and the peak. That is
/// about 43,000 rows a month - a few megabytes of CSV that any spreadsheet can open.
/// Temperatures are stored in Celsius regardless of the display unit.
/// </summary>
public sealed class HistoryLog
{
    private const int RetentionDays = 30;

    private const string Header =
        "timestamp,cpu_load_avg,cpu_load_max,cpu_temp_avg,cpu_temp_max,cpu_watts_avg," +
        "gpu_load_avg,gpu_load_max,gpu_temp_avg,gpu_temp_max,gpu_watts_avg";

    /// <summary>Running average and peak for one measurement inside the current minute.</summary>
    private struct Accumulator
    {
        private int _count;
        private double _sum;
        private double _max;

        public void Add(float? value)
        {
            if (value is not float v) return;
            _count++;
            _sum += v;
            _max = _count == 1 ? v : Math.Max(_max, v);
        }

        public readonly string Average =>
            _count == 0 ? "" : (_sum / _count).ToString("0.#", CultureInfo.InvariantCulture);

        public readonly string Peak =>
            _count == 0 ? "" : _max.ToString("0.#", CultureInfo.InvariantCulture);

        public void Clear() => this = default;
    }

    private readonly string _path;

    private DateTimeOffset _bucket;      // start of the minute being accumulated
    private Accumulator _cpuLoad, _cpuTemp, _cpuWatts;
    private Accumulator _gpuLoad, _gpuTemp, _gpuWatts;
    private bool _hasSamples;
    private DateTime _lastPruneDay;

    public HistoryLog()
    {
        _path = Path.Combine(Settings.Directory0, "history.csv");
        Prune();
    }

    public string Path0 => _path;

    /// <summary>Folds one poll into the current minute, writing a row when the minute rolls over.</summary>
    public void Add(DeviceReading cpu, DeviceReading gpu, DateTimeOffset now)
    {
        var minute = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, now.Offset);

        if (_hasSamples && minute != _bucket)
        {
            WriteRow();
            ResetBucket();
        }

        _bucket = minute;
        _hasSamples = true;

        _cpuLoad.Add(cpu.Load);
        _cpuTemp.Add(cpu.Temp);
        _cpuWatts.Add(cpu.PowerWatts);
        _gpuLoad.Add(gpu.Load);
        _gpuTemp.Add(gpu.Temp);
        _gpuWatts.Add(gpu.PowerWatts);
    }

    /// <summary>Writes any part-finished minute; call before shutting down.</summary>
    public void Flush()
    {
        if (!_hasSamples) return;
        WriteRow();
        ResetBucket();
    }

    private void ResetBucket()
    {
        _cpuLoad.Clear();
        _cpuTemp.Clear();
        _cpuWatts.Clear();
        _gpuLoad.Clear();
        _gpuTemp.Clear();
        _gpuWatts.Clear();
        _hasSamples = false;
    }

    private void WriteRow()
    {
        try
        {
            Directory.CreateDirectory(Settings.Directory0);

            bool needHeader = !File.Exists(_path) || new FileInfo(_path).Length == 0;
            var line = new StringBuilder();
            if (needHeader) line.AppendLine(Header);

            line.Append(_bucket.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture))
                .Append(',').Append(_cpuLoad.Average)
                .Append(',').Append(_cpuLoad.Peak)
                .Append(',').Append(_cpuTemp.Average)
                .Append(',').Append(_cpuTemp.Peak)
                .Append(',').Append(_cpuWatts.Average)
                .Append(',').Append(_gpuLoad.Average)
                .Append(',').Append(_gpuLoad.Peak)
                .Append(',').Append(_gpuTemp.Average)
                .Append(',').Append(_gpuTemp.Peak)
                .Append(',').Append(_gpuWatts.Average);

            File.AppendAllText(_path, line.ToString() + Environment.NewLine);

            // One prune a day is enough; a month of rows is small and rewriting is cheap.
            if (_lastPruneDay != DateTime.UtcNow.Date) Prune();
        }
        catch (Exception ex)
        {
            HardwareMonitor.Log($"history write FAILED: {ex.Message}");
        }
    }

    /// <summary>Drops rows older than the retention window, rewriting only when something goes.</summary>
    public void Prune()
    {
        _lastPruneDay = DateTime.UtcNow.Date;

        try
        {
            if (!File.Exists(_path)) return;

            var cutoff = DateTimeOffset.Now.AddDays(-RetentionDays);
            var lines = File.ReadAllLines(_path);
            var kept = new List<string>(lines.Length) { Header };
            int dropped = 0;

            foreach (var line in lines)
            {
                if (line.Length == 0 || line.StartsWith("timestamp", StringComparison.Ordinal)) continue;

                int comma = line.IndexOf(',');
                if (comma <= 0) continue;

                if (DateTimeOffset.TryParse(line.AsSpan(0, comma), CultureInfo.InvariantCulture,
                                            DateTimeStyles.None, out var stamp)
                    && stamp < cutoff)
                {
                    dropped++;
                    continue;
                }

                kept.Add(line);
            }

            if (dropped == 0) return;

            // Write beside the original and swap, so a crash mid-write cannot lose the log.
            string temp = _path + ".tmp";
            File.WriteAllLines(temp, kept);
            File.Move(temp, _path, overwrite: true);

            HistoryLogged($"pruned {dropped} row(s) older than {RetentionDays} days");
        }
        catch (Exception ex)
        {
            HistoryLogged($"prune FAILED: {ex.Message}");
        }
    }

    private static void HistoryLogged(string message) => HardwareMonitor.Log("history: " + message);
}
