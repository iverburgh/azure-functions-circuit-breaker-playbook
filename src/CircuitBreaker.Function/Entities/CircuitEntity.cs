using CircuitBreaker.Contracts;
using CircuitBreaker.Core;
using CircuitBreaker.Function.Observability;
using CircuitBreaker.Function.Switches;
using Microsoft.Azure.Functions.Worker;
using Microsoft.DurableTask.Entities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CircuitBreaker.Function.Entities;

/// <summary>
/// One Durable Entity per circuit. The runtime runs its operations one at a time and persists the
/// state after each batch, which gives two guarantees for free: every state change is atomic, and it
/// survives restarts. The entity itself stays thin: decisions come from <see cref="CircuitEngine"/>;
/// the entity supplies the time, carries out the effects and flips the switch.
/// </summary>
/// <remarks>
/// After a crash the runtime runs the operations since the last checkpoint again. Everything here is
/// safe to repeat: the engine is deterministic given its input, and switches are idempotent.
/// </remarks>
public sealed class CircuitEntity(
    ICircuitSwitchProvider switches,
    IOptions<SwitchOptions> switchOptions,
    CircuitMetrics metrics,
    TimeProvider timeProvider,
    ILogger<CircuitEntity> logger) : TaskEntity<CircuitData>
{
    public const string Name = "Circuit";

    private const int MaxSwitchErrorLength = 500;

    public static EntityInstanceId IdFor(string circuitKey) => new(Name, circuitKey);

    [Function(Name)]
    public static Task Run([EntityTrigger] TaskEntityDispatcher dispatcher) => dispatcher.DispatchAsync<CircuitEntity>();

    private string Key => Context.Id.Key;

    public async Task Register(CircuitDefinition definition)
    {
        var now = timeProvider.GetUtcNow();
        Carry(CircuitOperations.Register, CircuitEngine.Register(State, definition, now));
        logger.CircuitRegistered(Key, definition.DisplayName, State.State);
        await SyncSwitchAsync(now);
    }

    public async Task ReportOutcomes(OutcomeReport report)
    {
        if (!IsRegistered(CircuitOperations.ReportOutcomes))
        {
            return;
        }

        var now = timeProvider.GetUtcNow();
        metrics.RecordOutcomes(Key, report);
        Carry(CircuitOperations.ReportOutcomes, CircuitEngine.ReportOutcomes(State, report, now));
        await SyncSwitchAsync(now);
    }

    public async Task<ProbePermit> TryAcquireProbe(ProbeRequest request)
    {
        if (!IsRegistered(CircuitOperations.TryAcquireProbe))
        {
            // An unknown circuit never blocks: the breaker must not become the outage.
            return new ProbePermit(true, CircuitState.Closed);
        }

        var now = timeProvider.GetUtcNow();
        var (permit, effects) = CircuitEngine.TryAcquireProbe(State, request, Guid.NewGuid(), now);
        Carry(CircuitOperations.TryAcquireProbe, effects);
        metrics.RecordProbeRequest(Key, permit);
        logger.ProbeRequested(Key, permit.Granted, permit.State, permit.ProbeId);
        await SyncSwitchAsync(now);
        return permit;
    }

    public async Task<ManualActionResult> ApplyManualAction(ManualActionCommand command)
    {
        if (!IsRegistered(CircuitOperations.ApplyManualAction))
        {
            return new ManualActionResult(false, "The circuit does not exist.");
        }

        var now = timeProvider.GetUtcNow();
        var (accepted, message, effects) = CircuitEngine.ApplyManualAction(State, command, now);
        logger.ManualAction(Key, command.Action, command.Actor, accepted, message, command.Reason);
        if (accepted && command.Action == CircuitAction.Delete)
        {
            logger.CircuitDeleted(Key, $"by {command.Actor}: {command.Reason}");
            State = null!;
            return new ManualActionResult(true, message);
        }

        Carry(CircuitOperations.ApplyManualAction, effects);
        await SyncSwitchAsync(now);
        return new ManualActionResult(accepted, message, CircuitEngine.ToSnapshot(State, now));
    }

    public async Task RecoveryTimeoutElapsed(RecoveryTimer timer)
    {
        if (!IsRegistered(CircuitOperations.RecoveryTimeoutElapsed))
        {
            return;
        }

        var now = timeProvider.GetUtcNow();
        Carry(CircuitOperations.RecoveryTimeoutElapsed, CircuitEngine.RecoveryTimeoutElapsed(State, timer, now));
        await SyncSwitchAsync(now);
    }

    public Task ProbeLeaseExpired(ProbeLeaseTimer timer)
    {
        if (IsRegistered(CircuitOperations.ProbeLeaseExpired))
        {
            Carry(CircuitOperations.ProbeLeaseExpired, CircuitEngine.ProbeLeaseExpired(State, timer, timeProvider.GetUtcNow()));
        }

        return Task.CompletedTask;
    }

    /// <summary>Retries a switch that could not be set. Only the retry with the expected due time runs; others are duplicates.</summary>
    public async Task ReconcileSwitch(DateTimeOffset dueAt)
    {
        if (!IsRegistered(CircuitOperations.ReconcileSwitch))
        {
            return;
        }

        if (State.Switch is null || State.Switch.RetryDueAt != dueAt)
        {
            logger.OperationIgnored(Key, CircuitOperations.ReconcileSwitch, $"Retry due at {dueAt:O} is no longer pending.");
            return;
        }

        State.Switch.RetryDueAt = null;
        await SyncSwitchAsync(timeProvider.GetUtcNow());
    }

    public Task CheckStaleness(DateTimeOffset dueAt)
    {
        if (!IsRegistered(CircuitOperations.CheckStaleness))
        {
            return Task.CompletedTask;
        }

        var (delete, effects) = CircuitEngine.CheckStaleness(State, dueAt, timeProvider.GetUtcNow());
        if (delete == true)
        {
            logger.CircuitDeleted(Key, $"no registration or outcome since {State.LastSeenAt:O}");
            State = null!;
            return Task.CompletedTask;
        }

        Carry(CircuitOperations.CheckStaleness, effects);
        return Task.CompletedTask;
    }

    private bool IsRegistered(string operation)
    {
        if (State?.Definition is not null)
        {
            return true;
        }

        // Do not let a stray signal create an empty entity: setting the state to null keeps it deleted.
        logger.UnknownCircuit(Key, operation);
        State = null!;
        return false;
    }

    private void Carry(string operation, CircuitEffects effects)
    {
        foreach (var transition in effects.Transitions)
        {
            metrics.RecordTransition(Key, transition);
            if (transition.To is CircuitState.Open or CircuitState.Isolated)
            {
                logger.CircuitStopped(Key, transition.From, transition.To, transition.Trigger, State.Generation, transition.Reason, transition.Actor);
            }
            else
            {
                logger.CircuitTransitioned(Key, transition.From, transition.To, transition.Trigger, State.Generation, transition.Reason, transition.Actor);
            }
        }

        foreach (var signal in effects.Signals)
        {
            Context.SignalEntity(Context.Id, signal.Operation, signal.Input, new SignalEntityOptions { SignalTime = signal.At });
        }

        if (effects.Ignored is not null)
        {
            logger.OperationIgnored(Key, operation, effects.Ignored);
        }
    }

    private async Task SyncSwitchAsync(DateTimeOffset now)
    {
        var definition = State.RequiredDefinition.Switch;
        var status = State.Switch;
        if (definition is null || status is null || status.InSync)
        {
            return;
        }

        status.LastAttemptAt = now;
        try
        {
            using var timeout = new CancellationTokenSource(switchOptions.Value.Timeout);
            await switches.Get(definition).SetAsync(status.Desired, timeout.Token);
            status.Applied = status.Desired;
            status.LastError = null;
            status.FailedAttempts = 0;
            status.RetryDueAt = null;
            metrics.RecordSwitchUpdate(Key, succeeded: true);
            logger.SwitchApplied(Key, definition.Target, status.Desired);
        }
        catch (Exception exception)
        {
            // The circuit state is still right; only the switch lags. Record it, retry later, never throw:
            // throwing would roll back the state change and lose the decision.
            status.FailedAttempts++;
            status.LastError = exception.Message.Length <= MaxSwitchErrorLength ? exception.Message : exception.Message[..MaxSwitchErrorLength];
            status.RetryDueAt = now + SwitchRetry.Delay(status.FailedAttempts);
            Context.SignalEntity(Context.Id, CircuitOperations.ReconcileSwitch, status.RetryDueAt.Value,
                new SignalEntityOptions { SignalTime = status.RetryDueAt.Value });
            metrics.RecordSwitchUpdate(Key, succeeded: false);
            logger.SwitchFailed(exception, Key, definition.Target, status.Desired, status.FailedAttempts, status.RetryDueAt.Value);
        }
    }
}
