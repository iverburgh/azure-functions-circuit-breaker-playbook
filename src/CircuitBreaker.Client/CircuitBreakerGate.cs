using CircuitBreaker.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CircuitBreaker.Client;

/// <summary>Guards calls to one dependency.</summary>
public interface ICircuitBreaker
{
    string Key { get; }

    /// <summary>Runs <paramref name="action"/> if the circuit allows it; otherwise throws <see cref="BrokenCircuitException"/> without running it.</summary>
    Task ExecuteAsync(Func<CancellationToken, Task> action, CancellationToken cancellationToken = default);

    Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken = default);

    /// <summary>As above, and judges a returned result too — for APIs that return errors instead of throwing them.</summary>
    Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> action, Func<T, Outcome> classifyResult, CancellationToken cancellationToken = default);

    /// <summary>For full control: get a permit, make the call yourself, report the outcome on the permit.</summary>
    ValueTask<CircuitPermit> AcquireAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Permission for one call. Report its outcome exactly once; disposing an unreported permit reports it
/// as ignored, which releases a probe slot without a verdict.
/// </summary>
public sealed class CircuitPermit : IDisposable
{
    private readonly Action<Outcome, Guid?> _report;
    private int _reported;

    internal CircuitPermit(Action<Outcome, Guid?> report, Guid? probeId)
    {
        _report = report;
        ProbeId = probeId;
    }

    public Guid? ProbeId { get; }

    public bool IsProbe => ProbeId is not null;

    public void Report(Outcome outcome)
    {
        if (Interlocked.Exchange(ref _reported, 1) == 0)
        {
            _report(outcome, ProbeId);
        }
    }

    public void Dispose() => Report(Outcome.Ignored);
}

/// <summary>
/// The client-side gate. It decides locally from a cached copy of the circuit state, so a closed
/// circuit adds no network call to your request, and an open one refuses at once. Only while half-open
/// does it ask the breaker, for one of the few probe permits.
/// </summary>
internal sealed partial class CircuitBreakerGate(
    CircuitOptions circuit,
    ICircuitBreakerApi api,
    OutcomeReporter reporter,
    RegistrationSignal registration,
    IOptions<CircuitBreakerClientOptions> clientOptions,
    TimeProvider timeProvider,
    ILogger<CircuitBreakerGate> logger) : ICircuitBreaker
{
    private readonly SemaphoreSlim _refresh = new(1, 1);
    private KnownState? _known;

    public string Key => circuit.Definition.Key;

    public Task ExecuteAsync(Func<CancellationToken, Task> action, CancellationToken cancellationToken = default) =>
        ExecuteAsync<bool>(async token =>
        {
            await action(token);
            return true;
        }, cancellationToken);

    public Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken = default) =>
        ExecuteAsync(action, static _ => Outcome.Success, cancellationToken);

    public async Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> action, Func<T, Outcome> classifyResult, CancellationToken cancellationToken = default)
    {
        using var permit = await AcquireAsync(cancellationToken);
        using var attemptTimeout = new CancellationTokenSource(circuit.AttemptTimeout, timeProvider);
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, attemptTimeout.Token);
        try
        {
            var result = await action(attempt.Token);
            permit.Report(classifyResult(result));
            return result;
        }
        catch (Exception exception)
        {
            var timedOut = attemptTimeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested;
            permit.Report(circuit.Classifier.Classify(exception, timedOut, cancellationToken.IsCancellationRequested));
            if (timedOut && exception is OperationCanceledException)
            {
                throw new TimeoutException($"The call through circuit '{Key}' exceeded its attempt timeout of {circuit.AttemptTimeout}.", exception);
            }

            throw;
        }
    }

    public async ValueTask<CircuitPermit> AcquireAsync(CancellationToken cancellationToken = default)
    {
        var known = await GetKnownStateAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        switch (known.State)
        {
            case null when clientOptions.Value.WhenBreakerUnavailable == BreakerUnavailableBehavior.Allow:
            case CircuitState.Closed or CircuitState.Disabled:
                return new CircuitPermit(Report, probeId: null);
            case null:
                throw new BrokenCircuitException(Key, null, null, $"Circuit '{Key}': the breaker is unreachable and calls are configured to be refused.");
            case CircuitState.HalfOpen:
            case CircuitState.Open when known.RecoveryAt <= now:
                return await RequestProbeAsync(cancellationToken);
            case CircuitState.Open:
                throw new BrokenCircuitException(Key, CircuitState.Open, known.RecoveryAt, $"Circuit '{Key}' is open until {known.RecoveryAt:O}.");
            default:
                throw new BrokenCircuitException(Key, known.State, null, $"Circuit '{Key}' is {known.State}.");
        }
    }

    private void Report(Outcome outcome, Guid? probeId) => reporter.Record(Key, outcome, probeId);

    private async Task<CircuitPermit> RequestProbeAsync(CancellationToken cancellationToken)
    {
        ProbePermit permit;
        try
        {
            using var timeout = new CancellationTokenSource(clientOptions.Value.BreakerCallTimeout, timeProvider);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
            permit = await api.RequestProbeAsync(Key, new ProbeRequest(clientOptions.Value.Source), linked.Token);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            // The dependency was failing a moment ago; without a permit, staying away is the safe choice.
            LogProbeRequestFailed(exception, Key);
            throw new BrokenCircuitException(Key, CircuitState.HalfOpen, timeProvider.GetUtcNow().AddSeconds(1),
                $"Circuit '{Key}' is recovering and no probe permit could be obtained.");
        }

        _known = new KnownState(permit.State, permit.State == CircuitState.Open ? permit.RetryAfter : null, timeProvider.GetUtcNow());
        if (!permit.Granted)
        {
            throw new BrokenCircuitException(Key, permit.State, permit.RetryAfter,
                $"Circuit '{Key}' is {permit.State}; no probe permit is available.");
        }

        return new CircuitPermit(Report, permit.ProbeId);
    }

    private async ValueTask<KnownState> GetKnownStateAsync(CancellationToken cancellationToken)
    {
        var known = _known;
        if (known is not null && timeProvider.GetUtcNow() - known.FetchedAt < clientOptions.Value.SnapshotTtl)
        {
            return known;
        }

        // One refresh at a time. Others keep using the stale state instead of queueing behind it.
        if (known is not null && !await _refresh.WaitAsync(0, cancellationToken))
        {
            return known;
        }

        if (known is null)
        {
            await _refresh.WaitAsync(cancellationToken);
        }

        try
        {
            if (_known is { } fresh && timeProvider.GetUtcNow() - fresh.FetchedAt < clientOptions.Value.SnapshotTtl)
            {
                return fresh;
            }

            _known = await FetchAsync(cancellationToken);
            return _known;
        }
        finally
        {
            _refresh.Release();
        }
    }

    private async Task<KnownState> FetchAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = new CancellationTokenSource(clientOptions.Value.BreakerCallTimeout, timeProvider);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
            var snapshot = await api.GetAsync(Key, linked.Token);
            if (snapshot is null)
            {
                // Not registered (yet), or deleted as stale. Allow calls and get it registered.
                registration.Request();
                return new KnownState(CircuitState.Closed, null, timeProvider.GetUtcNow());
            }

            return new KnownState(snapshot.State, snapshot.RecoveryAt, timeProvider.GetUtcNow());
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            LogBreakerUnreachable(exception, Key);

            // Keep using what we knew; if we knew nothing, remember that for one TTL so we do not hammer the breaker.
            return _known is { } previous
                ? previous with { FetchedAt = timeProvider.GetUtcNow() }
                : new KnownState(null, null, timeProvider.GetUtcNow());
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not read circuit {CircuitKey} from the breaker; using the last known state")]
    private partial void LogBreakerUnreachable(Exception exception, string circuitKey);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not obtain a probe permit for circuit {CircuitKey}")]
    private partial void LogProbeRequestFailed(Exception exception, string circuitKey);

    private sealed record KnownState(CircuitState? State, DateTimeOffset? RecoveryAt, DateTimeOffset FetchedAt);
}
