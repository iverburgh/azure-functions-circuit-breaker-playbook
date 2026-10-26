using CircuitBreaker.Contracts;

namespace CircuitBreaker.Client.Tests;

public sealed class FailFastTests
{
    [Test]
    public async Task A_closed_circuit_runs_the_call_and_reads_the_state_once_per_ttl()
    {
        var fixture = new Fixture();
        fixture.Api.SetState(CircuitState.Closed);

        for (var i = 0; i < 100; i++)
        {
            await fixture.Breaker.ExecuteAsync(_ => Task.CompletedTask);
        }

        Assert.That(fixture.Api.Gets, Is.EqualTo(1), "Closed adds no network call per request.");
        fixture.Time.Advance(TimeSpan.FromSeconds(5));
        await fixture.Breaker.ExecuteAsync(_ => Task.CompletedTask);
        Assert.That(fixture.Api.Gets, Is.EqualTo(2), "After the TTL the state is read again.");
    }

    [Test]
    public void An_open_circuit_refuses_at_once_without_calling_the_dependency()
    {
        var fixture = new Fixture();
        var recoveryAt = Fixture.T0.AddSeconds(30);
        fixture.Api.SetState(CircuitState.Open, recoveryAt);
        var called = false;

        var refused = Assert.ThrowsAsync<BrokenCircuitException>(() => fixture.Breaker.ExecuteAsync(_ =>
        {
            called = true;
            return Task.CompletedTask;
        }));

        Assert.That(called, Is.False);
        Assert.That(refused!.State, Is.EqualTo(CircuitState.Open));
        Assert.That(refused.RetryAfter, Is.EqualTo(recoveryAt));
        Assert.That(fixture.Api.ProbeRequests, Is.Zero);
    }

    [Test]
    public void An_isolated_circuit_refuses_until_an_operator_resets_it()
    {
        var fixture = new Fixture();
        fixture.Api.SetState(CircuitState.Isolated);

        var refused = Assert.ThrowsAsync<BrokenCircuitException>(() => fixture.Breaker.ExecuteAsync(_ => Task.CompletedTask));

        Assert.That(refused!.State, Is.EqualTo(CircuitState.Isolated));
        Assert.That(refused.RetryAfter, Is.Null);
    }

    [Test]
    public async Task A_disabled_circuit_lets_calls_through()
    {
        var fixture = new Fixture();
        fixture.Api.SetState(CircuitState.Disabled);

        Assert.That(await fixture.Breaker.ExecuteAsync(_ => Task.FromResult(42)), Is.EqualTo(42));
    }

    [Test]
    public async Task Once_the_recovery_time_has_passed_the_client_asks_for_a_probe()
    {
        var fixture = new Fixture();
        fixture.Api.SetState(CircuitState.Open, Fixture.T0.AddSeconds(2));
        Assert.ThrowsAsync<BrokenCircuitException>(() => fixture.Breaker.ExecuteAsync(_ => Task.CompletedTask));

        fixture.Time.Advance(TimeSpan.FromSeconds(2));
        await fixture.Breaker.ExecuteAsync(_ => Task.CompletedTask);

        Assert.That(fixture.Api.ProbeRequests, Is.EqualTo(1));
    }

    [Test]
    public async Task Half_open_calls_need_a_probe_permit_and_report_against_it()
    {
        var fixture = new Fixture();
        fixture.Api.SetState(CircuitState.HalfOpen);
        var probeId = Guid.NewGuid();
        fixture.Api.NextPermit = new ProbePermit(true, CircuitState.HalfOpen, probeId, Fixture.T0.AddSeconds(30));

        await fixture.Breaker.ExecuteAsync(_ => Task.CompletedTask);

        var report = await fixture.FlushSingleReportAsync();
        Assert.That(report.ProbeResults.Single(), Is.EqualTo(new ProbeResult(probeId, true)));
        Assert.That(report.Successes, Is.Zero, "Probe outcomes are reported only as probe results.");
    }

    [Test]
    public void Without_a_probe_permit_a_half_open_circuit_refuses()
    {
        var fixture = new Fixture();
        fixture.Api.SetState(CircuitState.HalfOpen);
        fixture.Api.NextPermit = new ProbePermit(false, CircuitState.HalfOpen, RetryAfter: Fixture.T0.AddSeconds(5));
        var called = false;

        var refused = Assert.ThrowsAsync<BrokenCircuitException>(() => fixture.Breaker.ExecuteAsync(_ =>
        {
            called = true;
            return Task.CompletedTask;
        }));

        Assert.That(called, Is.False);
        Assert.That(refused!.RetryAfter, Is.EqualTo(Fixture.T0.AddSeconds(5)));
    }

    [Test]
    public void When_the_breaker_is_unreachable_while_half_open_the_client_stays_away()
    {
        var fixture = new Fixture();
        fixture.Api.SetState(CircuitState.HalfOpen);
        fixture.Breaker.AcquireAsync().AsTask().Wait(); // learn the state
        fixture.Api.Unreachable = true;

        Assert.ThrowsAsync<BrokenCircuitException>(() => fixture.Breaker.ExecuteAsync(_ => Task.CompletedTask));
    }

    [Test]
    public async Task When_the_breaker_is_unreachable_and_nothing_is_known_calls_are_allowed_by_default()
    {
        var fixture = new Fixture();
        fixture.Api.Unreachable = true;

        Assert.That(await fixture.Breaker.ExecuteAsync(_ => Task.FromResult("ok")), Is.EqualTo("ok"));
    }

    [Test]
    public void When_the_breaker_is_unreachable_and_nothing_is_known_calls_can_be_refused_instead()
    {
        var fixture = new Fixture(BreakerUnavailableBehavior.Reject);
        fixture.Api.Unreachable = true;

        var refused = Assert.ThrowsAsync<BrokenCircuitException>(() => fixture.Breaker.ExecuteAsync(_ => Task.CompletedTask));

        Assert.That(refused!.State, Is.Null);
    }

    [Test]
    public async Task When_the_breaker_becomes_unreachable_the_last_known_state_still_applies()
    {
        var fixture = new Fixture();
        fixture.Api.SetState(CircuitState.Open, Fixture.T0.AddMinutes(10));
        Assert.ThrowsAsync<BrokenCircuitException>(() => fixture.Breaker.ExecuteAsync(_ => Task.CompletedTask));
        fixture.Api.Unreachable = true;

        fixture.Time.Advance(TimeSpan.FromSeconds(6));
        Assert.ThrowsAsync<BrokenCircuitException>(() => fixture.Breaker.ExecuteAsync(_ => Task.CompletedTask));

        var getsWhileUnreachable = fixture.Api.Gets;
        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(async () =>
        {
            try
            {
                await fixture.Breaker.ExecuteAsync(_ => Task.CompletedTask);
            }
            catch (BrokenCircuitException)
            {
            }
        })));
        Assert.That(fixture.Api.Gets, Is.EqualTo(getsWhileUnreachable), "An unreachable breaker is not hammered: it is retried after the TTL.");
    }

    [Test]
    public async Task An_unknown_circuit_is_allowed_and_registration_is_requested()
    {
        var fixture = new Fixture();
        fixture.Api.Snapshot = null;

        await fixture.Breaker.ExecuteAsync(_ => Task.CompletedTask);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await fixture.Registration.WaitAsync(TimeSpan.FromHours(1), TimeProvider.System, timeout.Token);
        Assert.That(timeout.IsCancellationRequested, Is.False, "The registration signal was raised.");
    }
}
