using CircuitBreaker.Contracts;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace CircuitBreaker.Client.Tests;

public sealed class HealthCheckTests
{
    private static async Task<HealthCheckResult> CheckAsync(Fixture fixture)
    {
        var check = new CircuitBreakerHealthCheck([fixture.Circuit], fixture.Api);
        var context = new HealthCheckContext
        {
            Registration = new HealthCheckRegistration("circuit-breaker", check, HealthStatus.Unhealthy, tags: null),
        };
        return await check.CheckHealthAsync(context);
    }

    [Test]
    public async Task An_open_circuit_is_healthy_the_breaker_is_doing_its_job()
    {
        var fixture = new Fixture();
        fixture.Api.SetState(CircuitState.Open);

        var result = await CheckAsync(fixture);

        Assert.That(result.Status, Is.EqualTo(HealthStatus.Healthy));
        Assert.That(result.Data["payments-api"], Is.EqualTo("Open, Healthy"));
    }

    [Test]
    public async Task The_worst_circuit_health_is_reported_with_its_reasons()
    {
        var fixture = new Fixture();
        fixture.Api.SetState(CircuitState.Open);
        fixture.Api.Snapshot = fixture.Api.Snapshot! with
        {
            Health = new CircuitHealth(CircuitHealthStatus.Unhealthy, [new HealthReason(201, "Switch could not be set.")]),
        };

        var result = await CheckAsync(fixture);

        Assert.That(result.Status, Is.EqualTo(HealthStatus.Unhealthy));
        Assert.That(result.Description, Does.Contain("[201]"));
    }

    [Test]
    public async Task An_unreachable_breaker_is_degraded_not_unhealthy()
    {
        var fixture = new Fixture();
        fixture.Api.Unreachable = true;

        var result = await CheckAsync(fixture);

        Assert.That(result.Status, Is.EqualTo(HealthStatus.Degraded));
    }

    [Test]
    public async Task A_circuit_the_breaker_does_not_know_yet_is_degraded()
    {
        var fixture = new Fixture();

        var result = await CheckAsync(fixture);

        Assert.That(result.Status, Is.EqualTo(HealthStatus.Degraded));
        Assert.That(result.Data["payments-api"], Is.EqualTo("not registered"));
    }
}
