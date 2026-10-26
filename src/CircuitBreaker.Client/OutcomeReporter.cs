using System.Threading.Channels;
using CircuitBreaker.Contracts;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CircuitBreaker.Client;

/// <summary>
/// Collects outcomes in memory and sends one aggregated report per circuit per interval. Recording is
/// a counter increment under a lock: it never blocks a call and never fails it. Probe results are
/// flushed at once, because a waiting half-open circuit is waiting for them.
/// </summary>
internal sealed partial class OutcomeReporter(
    ICircuitBreakerApi api,
    IOptions<CircuitBreakerClientOptions> options,
    TimeProvider timeProvider,
    ILogger<OutcomeReporter> logger) : BackgroundService
{
    private readonly Lock _gate = new();
    private readonly Channel<bool> _flushNow = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private Dictionary<string, PendingReport> _pending = [];

    public void Record(string circuitKey, Outcome outcome, Guid? probeId)
    {
        if (outcome.Kind == OutcomeKind.Ignored && probeId is null)
        {
            return;
        }

        var now = timeProvider.GetUtcNow();
        lock (_gate)
        {
            if (!_pending.TryGetValue(circuitKey, out var pending))
            {
                pending = new PendingReport(now);
                _pending[circuitKey] = pending;
            }

            pending.Add(outcome, probeId, now);
        }

        if (probeId is not null)
        {
            _flushNow.Writer.TryWrite(true);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await WaitForNextFlushAsync(stoppingToken);
            await FlushAsync(stoppingToken);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        await FlushAsync(cancellationToken);
    }

    internal async Task FlushAsync(CancellationToken cancellationToken)
    {
        Dictionary<string, PendingReport> batch;
        lock (_gate)
        {
            if (_pending.Count == 0)
            {
                return;
            }

            batch = _pending;
            _pending = [];
        }

        var now = timeProvider.GetUtcNow();
        foreach (var (circuitKey, pending) in batch)
        {
            if (now - pending.FirstAt > options.Value.MaxReportAge)
            {
                LogDroppedStaleOutcomes(circuitKey, pending.FirstAt);
                continue;
            }

            try
            {
                using var timeout = new CancellationTokenSource(options.Value.BreakerCallTimeout, timeProvider);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
                await api.ReportAsync(circuitKey, pending.ToReport(options.Value.Source), linked.Token);
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                LogReportFailed(exception, circuitKey);
                lock (_gate)
                {
                    _pending[circuitKey] = _pending.TryGetValue(circuitKey, out var newer) ? pending.Merge(newer) : pending;
                }
            }
        }
    }

    private async Task WaitForNextFlushAsync(CancellationToken cancellationToken)
    {
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var interval = Task.Delay(options.Value.ReportInterval, timeProvider, wait.Token);
        var urgent = _flushNow.Reader.ReadAsync(wait.Token).AsTask();
        await Task.WhenAny(interval, urgent);
        await wait.CancelAsync();
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not report outcomes for circuit {CircuitKey}; they are kept for the next attempt")]
    private partial void LogReportFailed(Exception exception, string circuitKey);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Dropped outcomes for circuit {CircuitKey} collected since {FirstAt}: too old to count in the failure window")]
    private partial void LogDroppedStaleOutcomes(string circuitKey, DateTimeOffset firstAt);

    private sealed class PendingReport(DateTimeOffset firstAt)
    {
        public DateTimeOffset FirstAt { get; private set; } = firstAt;

        private int _successes;
        private readonly Dictionary<FailureCategory, int> _failures = [];
        private readonly List<ProbeResult> _probes = [];
        private FailureSample? _lastFailure;

        public void Add(Outcome outcome, Guid? probeId, DateTimeOffset now)
        {
            if (outcome.Kind == OutcomeKind.Failure)
            {
                _lastFailure = new FailureSample(outcome.Category!.Value, outcome.Message ?? outcome.Category.Value.ToString(), now);
            }

            if (probeId is { } id)
            {
                _probes.Add(new ProbeResult(id, outcome.Kind == OutcomeKind.Success, outcome.Kind == OutcomeKind.Failure ? outcome.Category : null));
                return;
            }

            if (outcome.Kind == OutcomeKind.Success)
            {
                _successes++;
            }
            else
            {
                _failures[outcome.Category!.Value] = _failures.GetValueOrDefault(outcome.Category.Value) + 1;
            }
        }

        public PendingReport Merge(PendingReport newer)
        {
            FirstAt = FirstAt < newer.FirstAt ? FirstAt : newer.FirstAt;
            _successes += newer._successes;
            foreach (var (category, count) in newer._failures)
            {
                _failures[category] = _failures.GetValueOrDefault(category) + count;
            }

            _probes.AddRange(newer._probes);
            _lastFailure = newer._lastFailure ?? _lastFailure;
            return this;
        }

        public OutcomeReport ToReport(string source) => new()
        {
            Successes = _successes,
            Failures = [.. _failures.Select(f => new FailureCount(f.Key, f.Value))],
            ProbeResults = [.. _probes],
            LastFailure = _lastFailure,
            Source = source,
        };
    }
}
