using System.Diagnostics;

namespace Caverarria;

internal static class FramePerformance
{
    private sealed class Counter
    {
        public long Ticks, Maximum;
        public int Count;
    }
    private static readonly Dictionary<string, Counter> counters = new();
    private static readonly object gate = new();
    private static object? report;
    private static long window = Stopwatch.GetTimestamp();
    public static long Begin() => Stopwatch.GetTimestamp();
    public static void End(string name, long started)
    {
        lock (gate)
        {
        if (!counters.TryGetValue(name, out var counter)) counters[name] = counter = new();
        long ticks = Stopwatch.GetTimestamp() - started;
        counter.Ticks += ticks; counter.Count++; counter.Maximum = Math.Max(counter.Maximum, ticks);
        }
    }
    public static object? Report()
    {
        lock (gate)
        {
        long now = Stopwatch.GetTimestamp();
        double seconds = (now - window) / (double)Stopwatch.Frequency;
        if (seconds < 1) return report;
        report = counters.ToDictionary(pair => pair.Key, pair => new
        {
            milliseconds = pair.Value.Count == 0 ? 0 : pair.Value.Ticks * 1000.0 / Stopwatch.Frequency / pair.Value.Count,
            maximumMilliseconds = pair.Value.Maximum * 1000.0 / Stopwatch.Frequency,
            callsPerSecond = pair.Value.Count / seconds
        });
        counters.Clear(); window = now;
        return report;
        }
    }
}
