using CircuitBreaker.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace CircuitBreaker.Client.Tests;

/// <summary>An in-memory stand-in for the breaker's API, with a state you set and calls you can count.</summary>
internal sealed class FakeBreakerApi : ICircuitBreakerApi
{
    public CircuitSnapshot? Snapshot { get; set; }

    public ProbePermit NextPermit { get; set; } = new(true, CircuitState.HalfOpen, Guid.NewGuid());

    public bool Unreachable { get; set; }

    public int Gets { get; private set; }

    public int ProbeRequests { get; private set; }

    public List<CircuitDefinition> Registered { get; } = [];

    public HashSet<string> FailRegistrationFor { get; } = [];

    public List<(string Key, OutcomeReport Report)> Reports { get; } = [];

    public int FailReports { get; set; }

    public Task RegisterAsync(CircuitDefinition definition, CancellationToken cancellationToken)
    {
        if (Unreachable || FailRegistrationFor.Contains(definition.Key))
        {
            throw new HttpRequestException("Breaker unreachable");
        }

        Registered.Add(definition);
        return Task.CompletedTask;
    }

    public Task<CircuitSnapshot?> GetAsync(string circuitKey, CancellationToken cancellationToken)
    {
        Gets++;
        return Unreachable ? throw new HttpRequestException("Breaker unreachable") : Task.FromResult(Snapshot);
    }

    public Task<ProbePermit> RequestProbeAsync(string circuitKey, ProbeRequest request, CancellationToken cancellationToken)
    {
        ProbeRequests++;
        return Unreachable ? throw new HttpRequestException("Breaker unreachable") : Task.FromResult(NextPermit);
    }

    public Task ReportAsync(string circuitKey, OutcomeReport report, CancellationToken cancellationToken)
    {
        if (Unreachable || FailReports-- > 0)
        {
            throw new HttpRequestException("Breaker unreachable");
        }

        Reports.Add((circuitKey, report));
        return Task.CompletedTask;
    }

    public void SetState(CircuitState state, DateTimeOffset? recoveryAt = null) => Snapshot = new CircuitSnapshot
    {
        Key = Fixture.Definition.Key,
        DisplayName = Fixture.Definition.DisplayName,
        State = state,
        Generation = 1,
        StateChangedAt = Fixture.T0,
        LastSeenAt = Fixture.T0,
        FailuresInWindow = 0,
        SuccessesInWindow = 0,
        IgnoredFailuresInWindow = 0,
        RecoveryAt = recoveryAt,
        ConsecutiveReopens = 0,
        ActiveProbes = 0,
        ProbeSuccesses = 0,
        AllowedActions = [],
        History = [],
        Definition = Fixture.Definition,
    };
}

internal sealed class Fixture
{
    public static readonly DateTimeOffset T0 = new(2026, 1, 5, 9, 0, 0, TimeSpan.Zero);

    public static readonly CircuitDefinition Definition = new() { Key = "payments-api", DisplayName = "Payments API" };

    public Fixture(BreakerUnavailableBehavior whenUnavailable = BreakerUnavailableBehavior.Allow, IFailureClassifier? classifier = null)
    {
        Options = Microsoft.Extensions.Options.Options.Create(new CircuitBreakerClientOptions
        {
            ServiceUri = new Uri("https://breaker.example.com/"),
            WhenBreakerUnavailable = whenUnavailable,
            Source = "test-instance",
        });
        Reporter = new OutcomeReporter(Api, Options, Time, NullLogger<OutcomeReporter>.Instance);
        Circuit = new CircuitOptions { Definition = Definition, AttemptTimeout = TimeSpan.FromSeconds(2), Classifier = classifier ?? new DefaultFailureClassifier(typeof(ArgumentException)) };
        Breaker = new CircuitBreakerGate(Circuit, Api, Reporter, Registration, Options, Time, NullLogger<CircuitBreakerGate>.Instance);
    }

    public FakeBreakerApi Api { get; } = new();

    public FakeTimeProvider Time { get; } = new(T0);

    public IOptions<CircuitBreakerClientOptions> Options { get; }

    public OutcomeReporter Reporter { get; }

    public RegistrationSignal Registration { get; } = new();

    public CircuitOptions Circuit { get; }

    public CircuitBreakerGate Breaker { get; }

    /// <summary>Sends what the reporter collected and returns the single report it sent.</summary>
    public async Task<OutcomeReport> FlushSingleReportAsync()
    {
        Api.Reports.Clear();
        await Reporter.FlushAsync(CancellationToken.None);
        return Api.Reports.Single().Report;
    }
}
