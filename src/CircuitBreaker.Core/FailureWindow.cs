namespace CircuitBreaker.Core;

/// <summary>
/// A sliding window of outcome counts, kept as ten buckets per window. Memory stays constant no
/// matter how many calls are made, and the window is accurate to one bucket (a tenth of its length).
/// </summary>
public static class FailureWindow
{
    public const int BucketsPerWindow = 10;

    public static TimeSpan BucketLength(TimeSpan window) =>
        TimeSpan.FromTicks(Math.Max(TimeSpan.TicksPerSecond, window.Ticks / BucketsPerWindow));

    public static void Record(CircuitData data, DateTimeOffset now, int successes, int countedFailures, int ignoredFailures)
    {
        var window = data.RequiredDefinition.FailureWindow;
        Prune(data, now, window);
        if (successes == 0 && countedFailures == 0 && ignoredFailures == 0)
        {
            return;
        }

        var start = BucketStart(now, window);
        var bucket = data.Window.Find(b => b.Start == start);
        if (bucket is null)
        {
            bucket = new WindowBucket { Start = start };
            data.Window.Add(bucket);
        }

        bucket.Successes += successes;
        bucket.CountedFailures += countedFailures;
        bucket.IgnoredFailures += ignoredFailures;
    }

    public static WindowTotals Totals(CircuitData data, DateTimeOffset now)
    {
        if (data.Definition is null)
        {
            return default;
        }

        var window = data.Definition.FailureWindow;
        var length = BucketLength(window);
        var live = data.Window.Where(b => b.Start + length > now - window).ToList();
        return new WindowTotals(live.Sum(b => b.Successes), live.Sum(b => b.CountedFailures), live.Sum(b => b.IgnoredFailures));
    }

    private static void Prune(CircuitData data, DateTimeOffset now, TimeSpan window)
    {
        var length = BucketLength(window);
        data.Window.RemoveAll(b => b.Start + length <= now - window);
    }

    private static DateTimeOffset BucketStart(DateTimeOffset now, TimeSpan window)
    {
        var length = BucketLength(window).Ticks;
        var ticks = now.UtcTicks - (now.UtcTicks % length);
        return new DateTimeOffset(ticks, TimeSpan.Zero);
    }
}
