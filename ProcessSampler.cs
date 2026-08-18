using System.Diagnostics;

namespace CpuWidget;

/// <summary>
/// Ranks processes by CPU use, measured from the change in each process's total processor
/// time between two samples. Windows has no instantaneous per-process CPU figure, so the
/// first sample only establishes a baseline and returns nothing.
/// </summary>
public sealed class ProcessSampler
{
    private readonly Dictionary<int, TimeSpan> _previous = new();
    private DateTime _previousAt;

    public readonly record struct Entry(string Name, double Percent);

    /// <summary>Discards the baseline so the next sample starts a fresh measurement.</summary>
    public void Reset()
    {
        _previous.Clear();
        _previousAt = default;
    }

    /// <summary>
    /// Returns the busiest processes since the previous call, aggregated by process name -
    /// a browser spread over twenty helper processes should read as one entry.
    /// </summary>
    public List<Entry> Sample(int top)
    {
        var now = DateTime.UtcNow;
        var current = new Dictionary<int, TimeSpan>();
        var names = new Dictionary<int, string>();

        foreach (var process in Process.GetProcesses())
        {
            try
            {
                current[process.Id] = process.TotalProcessorTime;
                names[process.Id] = process.ProcessName;
            }
            catch
            {
                // Protected or already-exited processes simply don't count.
            }
            finally
            {
                process.Dispose();
            }
        }

        var totals = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        double elapsedMs = (now - _previousAt).TotalMilliseconds;

        if (_previousAt != default && elapsedMs > 50)
        {
            // A process can use one core per elapsed millisecond, so full load across every
            // core is the denominator for 100%.
            double denominator = elapsedMs * Environment.ProcessorCount;

            foreach (var (pid, time) in current)
            {
                if (!_previous.TryGetValue(pid, out var before)) continue;

                double deltaMs = (time - before).TotalMilliseconds;
                if (deltaMs <= 0) continue;

                string name = names[pid];
                totals[name] = totals.GetValueOrDefault(name) + 100.0 * deltaMs / denominator;
            }
        }

        _previous.Clear();
        foreach (var (pid, time) in current) _previous[pid] = time;
        _previousAt = now;

        return totals
            .OrderByDescending(entry => entry.Value)
            .Take(top)
            .Select(entry => new Entry(entry.Key, entry.Value))
            .ToList();
    }
}
