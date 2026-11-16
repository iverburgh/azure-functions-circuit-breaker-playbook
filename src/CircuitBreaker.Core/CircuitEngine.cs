using CircuitBreaker.Contracts;
using static CircuitBreaker.Contracts.CircuitState;

namespace CircuitBreaker.Core;

/// <summary>
/// All decisions of the circuit breaker, as pure functions of the state, the input and the time.
/// No I/O, no clock, no randomness: the entity supplies "now" and carries out the returned effects.
/// That makes every rule testable in microseconds, and the entity a thin adapter.
/// </summary>
public static class CircuitEngine
{
    public static readonly TimeSpan MaxStalenessCheckInterval = TimeSpan.FromDays(1);

    private const int MaxFailureMessageLength = 500;

    public static CircuitEffects Register(CircuitData data, CircuitDefinition definition, DateTimeOffset now)
    {
        var effects = new CircuitEffects();
        var isNew = data.Definition is null;
        if (!isNew && data.Definition!.FailureWindow != definition.FailureWindow)
        {
            // Buckets are sized to the window; counts from a differently sized window would be misleading.
            data.Window.Clear();
        }

        data.Definition = definition;
        data.LastSeenAt = now;
        if (isNew)
        {
            data.State = Closed;
            data.StateChangedAt = now;
        }

        if (definition.Switch is null)
        {
            data.Switch = null;
        }
        else if (data.Switch is null || isNew || data.Switch.Target != definition.Switch.Target)
        {
            data.Switch = new SwitchState { Target = definition.Switch.Target, Desired = CircuitTransitions.SwitchPositionFor(data.State) };
        }

        if (data.NextStalenessCheckAt is null)
        {
            ScheduleStalenessCheck(data, now, effects);
        }

        SyncAlertPolling(data, definition, now, effects);
        return effects;
    }

    public static CircuitEffects ReportOutcomes(CircuitData data, OutcomeReport report, DateTimeOffset now)
    {
        var effects = new CircuitEffects();
        var definition = data.RequiredDefinition;
        data.LastSeenAt = now;
        if (report.IsEmpty)
        {
            return effects;
        }

        data.LastOutcomeAt = now;
        var counted = report.Failures.Where(f => Counts(definition, f.Category)).Sum(f => Math.Max(0, f.Count));
        var ignored = report.Failures.Where(f => !Counts(definition, f.Category)).Sum(f => Math.Max(0, f.Count));
        var probeSuccesses = report.ProbeResults.Count(p => IsProbeSuccess(definition, p));
        var probeCounted = report.ProbeResults.Count(p => !p.Succeeded && p.Category is { } category && Counts(definition, category));
        FailureWindow.Record(data, now, Math.Max(0, report.Successes) + probeSuccesses, counted + probeCounted, ignored);

        if (report.LastFailure is { } failure)
        {
            data.LastFailure = failure with { Message = Truncate(failure.Message) };
        }

        // A counted failure while half-open, outside a probe, comes from a caller that has not seen the
        // state change yet. It is still evidence that the dependency is unhealthy: back to open.
        if (data.State == HalfOpen && counted > 0)
        {
            Transition(data, CircuitTrigger.ProbeFailed, now, effects, "A failure was reported outside a probe.");
        }

        foreach (var probe in report.ProbeResults)
        {
            HandleProbeResult(data, probe, now, effects);
        }

        if (data.State == Closed)
        {
            var failures = FailureWindow.Totals(data, now).CountedFailures;
            if (failures >= definition.FailureThreshold)
            {
                Transition(data, CircuitTrigger.FailureThresholdReached, now, effects,
                    $"{failures} counted failures within {definition.FailureWindow}.");
            }
        }

        return effects;
    }

    public static (ProbePermit Permit, CircuitEffects Effects) TryAcquireProbe(CircuitData data, ProbeRequest request, Guid probeId, DateTimeOffset now)
    {
        var effects = new CircuitEffects();
        var definition = data.RequiredDefinition;
        data.LastSeenAt = now;
        ExpireProbeLeases(data, now, effects);

        switch (data.State)
        {
            case Closed or Disabled:
                return (new ProbePermit(true, data.State), effects);
            case Open:
                return (new ProbePermit(false, Open, RetryAfter: data.RecoveryAt), effects);
            case Isolated or OpenedByAlert or OpenedAfterAlert:
                // Only a person, or the alert, ends these states; a probe could not change anything.
                return (new ProbePermit(false, data.State), effects);
        }

        if (data.Probes.Count >= definition.HalfOpenMaxProbes)
        {
            return (new ProbePermit(false, HalfOpen, RetryAfter: data.Probes.Min(p => p.ExpiresAt)), effects);
        }

        var lease = new ProbeLease { Id = probeId, ExpiresAt = now + definition.ProbeTimeout, Source = request.Source };
        data.Probes.Add(lease);
        effects.Signals.Add(new ScheduledSignal(CircuitOperations.ProbeLeaseExpired, new ProbeLeaseTimer(probeId, data.Generation), lease.ExpiresAt));
        return (new ProbePermit(true, HalfOpen, probeId, lease.ExpiresAt), effects);
    }

    public static CircuitEffects RecoveryTimeoutElapsed(CircuitData data, RecoveryTimer timer, DateTimeOffset now)
    {
        var effects = new CircuitEffects();
        if (timer.Generation != data.Generation || data.State != Open)
        {
            effects.Ignored = $"Recovery timer for generation {timer.Generation} is stale; the circuit is {data.State} in generation {data.Generation}.";
            return effects;
        }

        Transition(data, CircuitTrigger.RecoveryTimeoutElapsed, now, effects, "Recovery timeout elapsed.");
        return effects;
    }

    public static CircuitEffects ProbeLeaseExpired(CircuitData data, ProbeLeaseTimer timer, DateTimeOffset now)
    {
        var effects = new CircuitEffects();
        if (timer.Generation != data.Generation || data.Probes.TrueForAll(p => p.Id != timer.ProbeId))
        {
            effects.Ignored = $"Probe {timer.ProbeId} already completed or belongs to an earlier generation.";
            return effects;
        }

        ExpireProbeLeases(data, now, effects);
        return effects;
    }

    public static (bool Accepted, string Message, CircuitEffects Effects) ApplyManualAction(CircuitData data, ManualActionCommand command, DateTimeOffset now)
    {
        var effects = new CircuitEffects();
        if (command.Action == CircuitAction.Delete)
        {
            return (true, "Circuit deleted.", effects);
        }

        var trigger = CircuitTransitions.TriggerFor(command.Action)
            ?? throw new ArgumentOutOfRangeException(nameof(command), command.Action, "Unknown action.");
        var from = data.State;
        return Transition(data, trigger, now, effects, command.Reason, command.Actor)
            ? (true, $"Circuit changed from {from} to {data.State}.", effects)
            : (false, $"{command.Action} is not allowed while the circuit is {data.State}.", effects);
    }

    /// <summary>Decides whether a scheduled staleness check deletes the circuit. Returns <c>null</c> for a duplicate check.</summary>
    public static (bool? Delete, CircuitEffects Effects) CheckStaleness(CircuitData data, DateTimeOffset dueAt, DateTimeOffset now)
    {
        var effects = new CircuitEffects();
        if (data.NextStalenessCheckAt != dueAt)
        {
            effects.Ignored = $"Staleness check due at {dueAt:O} was superseded by the one due at {data.NextStalenessCheckAt:O}.";
            return (null, effects);
        }

        if (now - data.LastSeenAt >= data.RequiredDefinition.StaleAfter)
        {
            return (true, effects);
        }

        ScheduleStalenessCheck(data, now, effects);
        return (false, effects);
    }

    /// <summary>Whether a scheduled poll is the pending one. Any other due time is a duplicate or belongs to an unlinked rule set.</summary>
    public static bool IsAlertPollDue(CircuitData data, DateTimeOffset dueAt) =>
        data.Definition is { AlertRuleIds.Count: > 0 } && data.Alerts?.NextPollAt == dueAt;

    /// <summary>Whether the entity should query Azure at all: only with linked rules and polling not off.</summary>
    public static bool ShouldQueryAlerts(CircuitData data, AlertPollSettings settings) =>
        data.Definition is { AlertRuleIds.Count: > 0 } && settings.Mode != AlertPollingMode.Off;

    /// <summary>
    /// Applies the result of an alert query. A failed query records the error and changes no state: an
    /// outage of the monitoring plane must not open or close circuits. A scheduled poll always schedules
    /// the next one, also in <see cref="AlertPollingMode.Off"/>, so switching the mode on needs no restart.
    /// </summary>
    /// <param name="result">Null when nothing was queried, because the mode is off.</param>
    /// <param name="scheduled">False for an on-demand check, which only reschedules a broken loop.</param>
    public static CircuitEffects ApplyAlertPoll(CircuitData data, AlertPollSettings settings, AlertPollResult? result, DateTimeOffset now, bool scheduled)
    {
        var effects = new CircuitEffects();
        var definition = data.RequiredDefinition;
        if (definition.AlertRuleIds.Count == 0)
        {
            effects.Ignored = "The circuit links no alert rules.";
            return effects;
        }

        var alerts = data.Alerts ??= new AlertPollState { Since = now, Interval = settings.Interval };
        alerts.Mode = settings.Mode;
        alerts.Interval = settings.Interval;

        if (settings.Mode == AlertPollingMode.Off)
        {
            alerts.Active = false;
            alerts.WouldOpen = false;
            alerts.TrippedBy.Clear();
            alerts.LastError = null;
            HandOverToPerson(data, now, effects, "Alert polling is off; a person decides when to close the circuit.");
        }
        else if (result?.Rules is not { } rules)
        {
            alerts.LastPolledAt = now;
            alerts.LastError = result?.Error ?? "The alert rules were not queried.";
        }
        else
        {
            var (statuses, trippedBy) = AlertEvaluator.Evaluate(definition.AlertRuleIds, rules);
            alerts.LastPolledAt = now;
            alerts.LastSucceededAt = now;
            alerts.LastError = null;
            alerts.Rules = [.. statuses];
            alerts.TrippedBy = [.. trippedBy];
            var firing = trippedBy.Count > 0;
            if (settings.Mode == AlertPollingMode.Active)
            {
                alerts.Active = firing;
                alerts.WouldOpen = false;
                if (firing)
                {
                    Transition(data, CircuitTrigger.AlertsActive, now, effects, $"Alert rule(s) firing: {string.Join(", ", trippedBy.Select(RuleName))}.");
                }
                else
                {
                    Transition(data, CircuitTrigger.AlertsCleared, now, effects, "All linked alert rules have resolved.");
                }
            }
            else
            {
                alerts.Active = false;
                alerts.WouldOpen = firing;
                HandOverToPerson(data, now, effects, "Alert polling is in shadow mode; a person decides when to close the circuit.");
            }
        }

        if (scheduled || IsPollChainBroken(alerts, now))
        {
            ScheduleAlertPoll(data, settings, now, effects);
        }

        return effects;
    }

    public static CircuitHealth Health(CircuitData data, DateTimeOffset now) => CircuitHealthRules.Evaluate(data, now);

    public static CircuitSnapshot ToSnapshot(CircuitData data, DateTimeOffset now)
    {
        var definition = data.RequiredDefinition;
        var totals = FailureWindow.Totals(data, now);
        return new CircuitSnapshot
        {
            Key = definition.Key,
            DisplayName = definition.DisplayName,
            State = data.State,
            Generation = data.Generation,
            StateChangedAt = data.StateChangedAt,
            LastOutcomeAt = data.LastOutcomeAt,
            LastSeenAt = data.LastSeenAt,
            FailuresInWindow = totals.CountedFailures,
            SuccessesInWindow = totals.Successes,
            IgnoredFailuresInWindow = totals.IgnoredFailures,
            RecoveryAt = data.RecoveryAt,
            ConsecutiveReopens = data.ConsecutiveReopens,
            ActiveProbes = data.Probes.Count,
            ProbeSuccesses = data.ProbeSuccesses,
            LastFailure = data.LastFailure,
            Switch = data.Switch is null || definition.Switch is null
                ? null
                : new SwitchStatus(definition.Switch.Kind, definition.Switch.Target, data.Switch.Desired, data.Switch.Applied,
                    data.Switch.LastError, data.Switch.LastAttemptAt, data.Switch.FailedAttempts),
            Alerts = definition.AlertRuleIds.Count == 0 || data.Alerts is not { } alerts
                ? null
                : new AlertStatus(alerts.Mode, alerts.Active, alerts.WouldOpen, [.. alerts.TrippedBy], [.. alerts.Rules],
                    alerts.LastPolledAt, alerts.LastSucceededAt, alerts.LastError, alerts.Interval),
            Health = Health(data, now),
            AllowedActions = CircuitTransitions.AllowedActions(data.State),
            History = [.. data.History],
            Definition = definition,
        };
    }

    private static void HandleProbeResult(CircuitData data, ProbeResult probe, DateTimeOffset now, CircuitEffects effects)
    {
        var lease = data.Probes.Find(p => p.Id == probe.ProbeId);
        if (data.State != HalfOpen || lease is null)
        {
            // From an earlier half-open period, already expired, or already decided by another result.
            return;
        }

        data.Probes.Remove(lease);
        var definition = data.RequiredDefinition;
        if (IsProbeSuccess(definition, probe))
        {
            data.ProbeSuccesses++;
            if (data.ProbeSuccesses >= definition.HalfOpenSuccessThreshold)
            {
                Transition(data, CircuitTrigger.ProbesSucceeded, now, effects, $"{data.ProbeSuccesses} successful probes.");
            }
        }
        else if (probe.Category is { } category)
        {
            Transition(data, CircuitTrigger.ProbeFailed, now, effects, $"Probe failed: {category}.");
        }

        // No category: the caller gave up before an answer. No verdict either way; the slot is simply released.
    }

    /// <summary>A probe succeeds when the call succeeded, or failed in a category that does not count: the dependency answered.</summary>
    private static bool IsProbeSuccess(CircuitDefinition definition, ProbeResult probe) =>
        probe.Succeeded || (probe.Category is { } category && !Counts(definition, category));

    /// <summary>
    /// Releases probe slots whose lease expired. Not a failure: the caller enforces its own timeout and
    /// reports it. A lease only expires when the caller crashed or never received the permit.
    /// </summary>
    private static void ExpireProbeLeases(CircuitData data, DateTimeOffset now, CircuitEffects effects)
    {
        if (data.State != HalfOpen)
        {
            return;
        }

        var released = data.Probes.RemoveAll(p => p.ExpiresAt <= now);
        if (released > 0)
        {
            effects.Ignored = $"Released {released} probe slot(s) whose lease expired without a result.";
        }
    }

    private static bool Transition(CircuitData data, CircuitTrigger trigger, DateTimeOffset now, CircuitEffects effects, string? reason = null, string? actor = null)
    {
        if (!CircuitTransitions.TryGetTarget(data.State, trigger, out var to))
        {
            return false;
        }

        var definition = data.RequiredDefinition;
        var from = data.State;
        data.State = to;
        data.Generation++;
        data.StateChangedAt = now;
        data.Probes.Clear();
        data.ProbeSuccesses = 0;
        data.RecoveryAt = null;

        switch (to)
        {
            case Closed:
                data.Window.Clear();
                data.ConsecutiveReopens = 0;
                break;
            case Open:
                if (from == HalfOpen)
                {
                    data.ConsecutiveReopens++;
                }

                data.RecoveryAt = now + RecoveryBackoff.Delay(definition, data.ConsecutiveReopens, data.Generation);
                effects.Signals.Add(new ScheduledSignal(CircuitOperations.RecoveryTimeoutElapsed, new RecoveryTimer(data.Generation), data.RecoveryAt.Value));
                break;
            case Isolated or Disabled or OpenedByAlert or OpenedAfterAlert:
                data.ConsecutiveReopens = 0;
                break;
        }

        if (data.Switch is not null)
        {
            data.Switch.Desired = CircuitTransitions.SwitchPositionFor(to);
        }

        var record = new TransitionRecord(from, to, trigger.ToString(), now, reason, actor);
        data.History.Insert(0, record);
        if (data.History.Count > CircuitData.MaxHistory)
        {
            data.History.RemoveRange(CircuitData.MaxHistory, data.History.Count - CircuitData.MaxHistory);
        }

        effects.Transitions.Add(record);
        return true;
    }

    private static void ScheduleStalenessCheck(CircuitData data, DateTimeOffset now, CircuitEffects effects)
    {
        var interval = data.RequiredDefinition.StaleAfter < MaxStalenessCheckInterval ? data.RequiredDefinition.StaleAfter : MaxStalenessCheckInterval;
        var dueAt = now + interval;
        data.NextStalenessCheckAt = dueAt;
        effects.Signals.Add(new ScheduledSignal(CircuitOperations.CheckStaleness, dueAt, dueAt));
    }

    /// <summary>The poll interval until the first poll tells the engine the configured one.</summary>
    public static readonly TimeSpan DefaultAlertPollInterval = TimeSpan.FromMinutes(1);

    private static void SyncAlertPolling(CircuitData data, CircuitDefinition definition, DateTimeOffset now, CircuitEffects effects)
    {
        if (definition.AlertRuleIds.Count == 0)
        {
            if (data.Alerts is not null)
            {
                // The pending poll finds no due time any more and is ignored: the loop ends by itself.
                data.Alerts = null;
                HandOverToPerson(data, now, effects, "All alert rules were unlinked; a person decides when to close the circuit.");
            }

            return;
        }

        var alerts = data.Alerts ??= new AlertPollState { Since = now, Interval = DefaultAlertPollInterval };
        alerts.Rules.RemoveAll(r => !definition.AlertRuleIds.Contains(r.RuleId, StringComparer.OrdinalIgnoreCase));
        alerts.TrippedBy.RemoveAll(id => !definition.AlertRuleIds.Contains(id, StringComparer.OrdinalIgnoreCase));

        // Registration (every client start-up) restarts a loop that was lost, for example after a roll-back and roll-forward.
        if (alerts.NextPollAt is null || IsPollChainBroken(alerts, now))
        {
            alerts.NextPollAt = now;
            effects.Signals.Add(new ScheduledSignal(CircuitOperations.PollAlerts, now, now));
        }
    }

    private static bool IsPollChainBroken(AlertPollState alerts, DateTimeOffset now) =>
        alerts.NextPollAt is not { } next || now - next > Max(alerts.Interval * 3, TimeSpan.FromMinutes(5));

    private static void ScheduleAlertPoll(CircuitData data, AlertPollSettings settings, DateTimeOffset now, CircuitEffects effects)
    {
        // Jitter keeps many circuits from querying Resource Graph in the same second, which it throttles.
        var spread = ((DeterministicJitter.Unit($"{data.RequiredDefinition.Key}:{now.UtcTicks}") * 2) - 1) * Math.Clamp(settings.Jitter, 0, 0.5);
        var dueAt = now + (settings.Interval * (1 + spread));
        data.Alerts!.NextPollAt = dueAt;
        effects.Signals.Add(new ScheduledSignal(CircuitOperations.PollAlerts, dueAt, dueAt));
    }

    /// <summary>A circuit held open by an alert that no longer drives it goes to a person, never straight to closed.</summary>
    private static void HandOverToPerson(CircuitData data, DateTimeOffset now, CircuitEffects effects, string reason)
    {
        if (data.State == OpenedByAlert)
        {
            Transition(data, CircuitTrigger.AlertsCleared, now, effects, reason);
        }
    }

    private static string RuleName(string ruleId) => ruleId[(ruleId.LastIndexOf('/') + 1)..];

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;

    private static bool Counts(CircuitDefinition definition, FailureCategory category) => definition.CountedCategories.Contains(category);

    private static string Truncate(string message) =>
        message.Length <= MaxFailureMessageLength ? message : message[..MaxFailureMessageLength];
}
