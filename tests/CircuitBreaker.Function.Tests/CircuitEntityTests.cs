using CircuitBreaker.Contracts;
using CircuitBreaker.Core;
using Microsoft.Extensions.Logging;
using static CircuitBreaker.Function.Tests.EntityHarness;

namespace CircuitBreaker.Function.Tests;

public sealed class CircuitEntityTests
{
    private EntityHarness _harness = null!;

    [SetUp]
    public void SetUp() => _harness = new EntityHarness();

    [TearDown]
    public void TearDown() => _harness.Dispose();

    [Test]
    public async Task Registering_stores_the_circuit_and_schedules_a_staleness_check()
    {
        await _harness.RegisterAsync();

        Assert.That(_harness.State!.State, Is.EqualTo(CircuitState.Closed));
        Assert.That(_harness.State.Definition!.DisplayName, Is.EqualTo("Payments API"));
        Assert.That(_harness.Pending.Single().Operation, Is.EqualTo(CircuitOperations.CheckStaleness));
    }

    [Test]
    public async Task The_state_is_stored_as_readable_json()
    {
        await _harness.RegisterAsync();
        await _harness.ReportAsync(Failures(3));

        Assert.That(_harness.PersistedJson, Does.Contain("\"State\":\"Open\""));
        Assert.That(_harness.PersistedJson, Does.Contain("\"Trigger\":\"FailureThresholdReached\""));
    }

    [Test]
    public async Task A_signal_for_an_unknown_circuit_does_not_create_it()
    {
        await _harness.ReportAsync(Failures(3));

        Assert.That(_harness.State, Is.Null);
        Assert.That(_harness.Logger.Collector.GetSnapshot(), Has.Some.Matches<Microsoft.Extensions.Logging.Testing.FakeLogRecord>(r => r.Id.Id == 1009));
    }

    [Test]
    public async Task An_unknown_circuit_never_blocks_a_caller()
    {
        var permit = await _harness.AcquireProbeAsync();

        Assert.That(permit.Granted, Is.True);
        Assert.That(_harness.State, Is.Null);
    }

    [Test]
    public async Task The_full_cycle_closed_open_half_open_closed_runs_on_timers_and_probes()
    {
        await _harness.RegisterAsync();
        await _harness.ReportAsync(Failures(3));
        Assert.That(_harness.State!.State, Is.EqualTo(CircuitState.Open));

        await _harness.AdvanceAsync(TimeSpan.FromSeconds(29));
        Assert.That(_harness.State!.State, Is.EqualTo(CircuitState.Open));

        await _harness.AdvanceAsync(TimeSpan.FromSeconds(1));
        Assert.That(_harness.State!.State, Is.EqualTo(CircuitState.HalfOpen));

        var first = await _harness.AcquireProbeAsync();
        var second = await _harness.AcquireProbeAsync();
        await _harness.ReportAsync(new OutcomeReport
        {
            ProbeResults = [new ProbeResult(first.ProbeId!.Value, true), new ProbeResult(second.ProbeId!.Value, true)],
        });

        Assert.That(_harness.State!.State, Is.EqualTo(CircuitState.Closed));
        Assert.That(_harness.State.History.Select(h => h.To), Is.EqualTo(new[] { CircuitState.Closed, CircuitState.HalfOpen, CircuitState.Open }));
    }

    [Test]
    public async Task Transitions_are_logged_with_stable_event_ids_and_counted()
    {
        await _harness.RegisterAsync();
        await _harness.ReportAsync(Failures(3));

        var opened = _harness.Logger.Collector.GetSnapshot().Single(r => r.Id.Id == 1002);
        Assert.That(opened.Level, Is.EqualTo(LogLevel.Warning));
        Assert.That(opened.Message, Does.Contain("from Closed to Open via FailureThresholdReached"));

        var measurement = _harness.Transitions.GetMeasurementSnapshot().Single();
        Assert.That(measurement.Tags["to"], Is.EqualTo("Open"));
        Assert.That(measurement.Tags["circuit"], Is.EqualTo("payments-api"));
    }

    [Test]
    public async Task Manual_actions_return_the_new_snapshot_and_delete_removes_the_circuit()
    {
        await _harness.RegisterAsync();

        var isolated = await _harness.ActAsync(CircuitAction.Isolate, "Incident in progress");
        Assert.That(isolated.Accepted, Is.True);
        Assert.That(isolated.Circuit!.State, Is.EqualTo(CircuitState.Isolated));
        Assert.That(isolated.Circuit.History[0].Actor, Is.EqualTo("operator"));

        var refused = await _harness.ActAsync(CircuitAction.Isolate);
        Assert.That(refused.Accepted, Is.False);

        var deleted = await _harness.ActAsync(CircuitAction.Delete, "Decommissioned");
        Assert.That(deleted.Accepted, Is.True);
        Assert.That(_harness.State, Is.Null);
    }

    [Test]
    public async Task Timers_still_pending_for_a_deleted_circuit_do_not_bring_it_back()
    {
        await _harness.RegisterAsync();
        await _harness.ReportAsync(Failures(3));
        await _harness.ActAsync(CircuitAction.Delete, "Decommissioned");

        await _harness.AdvanceAsync(TimeSpan.FromDays(2));

        Assert.That(_harness.State, Is.Null);
    }

    [Test]
    public async Task A_circuit_nobody_registers_or_reports_on_is_deleted_after_its_stale_period()
    {
        await _harness.RegisterAsync(Definition(d => d with { StaleAfter = TimeSpan.FromDays(2) }));

        await _harness.AdvanceAsync(TimeSpan.FromDays(1));
        Assert.That(_harness.State, Is.Not.Null);

        await _harness.AdvanceAsync(TimeSpan.FromDays(1));
        Assert.That(_harness.State, Is.Null);
    }
}
