using System.Diagnostics.Metrics;
using System.Text.Json;
using CircuitBreaker.Contracts;
using CircuitBreaker.Core;
using CircuitBreaker.Function.Entities;
using CircuitBreaker.Function.Observability;
using CircuitBreaker.Function.Switches;
using Microsoft.DurableTask;
using Microsoft.DurableTask.Entities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace CircuitBreaker.Function.Tests;

/// <summary>
/// Plays the part of the Durable Task runtime for one circuit entity, closely enough to test what
/// matters: operations run one at a time; state is stored as JSON and read back before every
/// operation; signals are only sent when the operation commits; a crash discards both; scheduled
/// signals are delivered when their time comes — and can be delivered twice.
/// </summary>
internal sealed class EntityHarness : IDisposable
{
    public static readonly DateTimeOffset T0 = new(2026, 1, 5, 9, 0, 0, TimeSpan.Zero);

    private static readonly JsonSerializerOptions Json = new();
    private readonly SemaphoreSlim _oneAtATime = new(1, 1);
    private readonly ServiceProvider _services;
    private string? _persistedState;

    public EntityHarness(string key = "payments-api")
    {
        Key = key;
        _services = new ServiceCollection().AddMetrics().BuildServiceProvider();
        Metrics = new CircuitMetrics(_services.GetRequiredService<IMeterFactory>());
        Transitions = new Microsoft.Extensions.Diagnostics.Metrics.Testing.MetricCollector<long>(
            _services.GetRequiredService<IMeterFactory>(), CircuitMetrics.MeterName, "circuitbreaker.transitions");
    }

    public string Key { get; }

    public FakeTimeProvider Time { get; } = new(T0);

    public FakeSwitch Switch { get; } = new();

    public FakeLogger<CircuitEntity> Logger { get; } = new();

    public CircuitMetrics Metrics { get; }

    public Microsoft.Extensions.Diagnostics.Metrics.Testing.MetricCollector<long> Transitions { get; }

    /// <summary>Signals committed and not yet delivered, in the order they were sent.</summary>
    public List<PendingSignal> Pending { get; } = [];

    public CircuitData? State => _persistedState is null ? null : JsonSerializer.Deserialize<CircuitData>(_persistedState, Json);

    public string? PersistedJson => _persistedState;

    public static CircuitDefinition Definition(Func<CircuitDefinition, CircuitDefinition>? customize = null)
    {
        var definition = new CircuitDefinition
        {
            Key = "payments-api",
            DisplayName = "Payments API",
            FailureThreshold = 3,
            RecoveryTimeout = TimeSpan.FromSeconds(30),
            RecoveryJitter = 0,
            HalfOpenMaxProbes = 2,
            HalfOpenSuccessThreshold = 2,
        };
        return customize is null ? definition : customize(definition);
    }

    public static OutcomeReport Failures(int count, FailureCategory category = FailureCategory.ServerError) =>
        new() { Failures = [new FailureCount(category, count)] };

    /// <summary>Runs one operation as the runtime would. With <paramref name="crashBeforeCommit"/>, the operation runs but nothing it did is kept.</summary>
    public async Task<object?> RunAsync(string operation, object? input = null, bool crashBeforeCommit = false)
    {
        await _oneAtATime.WaitAsync();
        try
        {
            var state = new FakeEntityState(_persistedState);
            var context = new FakeEntityContext(new EntityInstanceId(CircuitEntity.Name, Key));
            var entity = new CircuitEntity(new FakeSwitchProvider(Switch), Options.Create(new SwitchOptions()), Metrics, Time, Logger);
            var result = await ((ITaskEntity)entity).RunAsync(new FakeEntityOperation(operation, input, state, context));

            if (!crashBeforeCommit)
            {
                _persistedState = state.Json;
                Pending.AddRange(context.Signals);
            }

            return result;
        }
        finally
        {
            _oneAtATime.Release();
        }
    }

    public Task RegisterAsync(CircuitDefinition? definition = null) => RunAsync(CircuitOperations.Register, definition ?? Definition());

    public Task ReportAsync(OutcomeReport report) => RunAsync(CircuitOperations.ReportOutcomes, report);

    public async Task<ProbePermit> AcquireProbeAsync() => (ProbePermit)(await RunAsync(CircuitOperations.TryAcquireProbe, new ProbeRequest("test")))!;

    public async Task<ManualActionResult> ActAsync(CircuitAction action, string reason = "test") =>
        (ManualActionResult)(await RunAsync(CircuitOperations.ApplyManualAction, new ManualActionCommand(action, reason, "operator")))!;

    /// <summary>Moves time forward and delivers every signal that has come due, including signals those signals send.</summary>
    public async Task AdvanceAsync(TimeSpan by)
    {
        var until = Time.GetUtcNow() + by;
        while (Pending.Where(s => s.At <= until).OrderBy(s => s.At).FirstOrDefault() is { } next)
        {
            Pending.Remove(next);
            if (next.At > Time.GetUtcNow())
            {
                Time.SetUtcNow(next.At);
            }

            await RunAsync(next.Operation, next.Input);
        }

        Time.SetUtcNow(until);
    }

    public void Dispose()
    {
        Transitions.Dispose();
        _services.Dispose();
        _oneAtATime.Dispose();
    }

    internal sealed record PendingSignal(string Operation, JsonElement? Input, DateTimeOffset At);

    private sealed class FakeEntityState(string? json) : TaskEntityState
    {
        public string? Json { get; private set; } = json;

        public override bool HasState => Json is not null;

        public override object? GetState(Type type) => Json is null ? null : JsonSerializer.Deserialize(Json, type, EntityHarness.Json);

        public override void SetState(object? state) => Json = state is null ? null : JsonSerializer.Serialize(state, state.GetType(), EntityHarness.Json);
    }

    private sealed class FakeEntityContext(EntityInstanceId id) : TaskEntityContext
    {
        public List<PendingSignal> Signals { get; } = [];

        public override EntityInstanceId Id => id;

        public override void SignalEntity(EntityInstanceId target, string operationName, object? input = null, SignalEntityOptions? options = null)
        {
            Assert.That(target, Is.EqualTo(id), "The circuit only signals itself.");
            Signals.Add(new PendingSignal(operationName, input is null ? null : JsonSerializer.SerializeToElement(input, input.GetType(), Json), options?.SignalTime ?? DateTimeOffset.MinValue));
        }

        public override string ScheduleNewOrchestration(TaskName name, object? input = null, StartOrchestrationOptions? options = null) =>
            throw new NotSupportedException("The circuit entity does not start orchestrations.");
    }

    private sealed class FakeEntityOperation(string name, object? input, TaskEntityState state, TaskEntityContext context) : TaskEntityOperation
    {
        public override string Name => name;

        public override TaskEntityContext Context => context;

        public override TaskEntityState State => state;

        public override bool HasInput => input is not null;

        // Inputs travel as JSON, as they do in the runtime: a type that does not round-trip fails here.
        public override object? GetInput(Type inputType) => input switch
        {
            null => null,
            JsonElement element => element.Deserialize(inputType, Json),
            _ => JsonSerializer.Deserialize(JsonSerializer.Serialize(input, input.GetType(), Json), inputType, Json),
        };
    }

    private sealed class FakeSwitchProvider(FakeSwitch fakeSwitch) : ICircuitSwitchProvider
    {
        public ICircuitSwitch Get(SwitchDefinition definition) => fakeSwitch;
    }
}

/// <summary>An idempotent switch that remembers its position, counts calls and can be told to fail.</summary>
internal sealed class FakeSwitch : ICircuitSwitch
{
    public SwitchPosition? Position { get; private set; }

    public int Calls { get; private set; }

    public int Writes { get; private set; }

    public int FailNext { get; set; }

    public Task SetAsync(SwitchPosition position, CancellationToken cancellationToken)
    {
        Calls++;
        if (FailNext > 0)
        {
            FailNext--;
            throw new InvalidOperationException("Switch target unavailable");
        }

        if (Position != position)
        {
            Position = position;
            Writes++;
        }

        return Task.CompletedTask;
    }
}
