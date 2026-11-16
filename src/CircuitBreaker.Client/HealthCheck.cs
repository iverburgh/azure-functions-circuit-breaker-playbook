using CircuitBreaker.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace CircuitBreaker.Client;

/// <summary>
/// Reports whether the breaker can carry out what it decides for this application's circuits. An open
/// circuit is the breaker doing its job, not a health problem. Use it for readiness or a dashboard,
/// never for liveness: restarting the application fixes nothing in the breaker.
/// </summary>
public sealed class CircuitBreakerHealthCheck(IEnumerable<CircuitOptions> circuits, ICircuitBreakerApi api) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var data = new Dictionary<string, object>(StringComparer.Ordinal);
        var reasons = new List<string>();
        var worst = HealthStatus.Healthy;

        foreach (var circuit in circuits)
        {
            var key = circuit.Definition.Key;
            CircuitSnapshot? snapshot;
            try
            {
                snapshot = await api.GetAsync(key, cancellationToken);
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                // The gate handles an unreachable breaker as configured; the application still works.
                return HealthCheckResult.Degraded("The circuit breaker could not be reached.", exception, data);
            }

            if (snapshot is null)
            {
                data[key] = "not registered";
                reasons.Add($"{key}: not registered yet.");
                worst = Worse(worst, HealthStatus.Degraded);
                continue;
            }

            data[key] = $"{snapshot.State}, {snapshot.Health.Status}";
            reasons.AddRange(snapshot.Health.Reasons.Select(r => $"{key}: [{r.Code}] {r.Message}"));
            worst = Worse(worst, snapshot.Health.Status switch
            {
                CircuitHealthStatus.Unhealthy => HealthStatus.Unhealthy,
                CircuitHealthStatus.Degraded => HealthStatus.Degraded,
                _ => HealthStatus.Healthy,
            });
        }

        var description = reasons.Count == 0 ? null : string.Join(" ", reasons);
        return worst switch
        {
            HealthStatus.Unhealthy => new HealthCheckResult(context.Registration.FailureStatus, description, data: data),
            HealthStatus.Degraded => HealthCheckResult.Degraded(description, data: data),
            _ => HealthCheckResult.Healthy(description, data),
        };
    }

    private static HealthStatus Worse(HealthStatus a, HealthStatus b) => a < b ? a : b;
}

public static class CircuitBreakerHealthCheckExtensions
{
    /// <summary>Adds <see cref="CircuitBreakerHealthCheck"/> for the circuits this application registers.</summary>
    public static IHealthChecksBuilder AddCircuitBreaker(
        this IHealthChecksBuilder builder, string name = "circuit-breaker", HealthStatus? failureStatus = null, IEnumerable<string>? tags = null) =>
        builder.AddCheck<CircuitBreakerHealthCheck>(name, failureStatus, tags ?? ["ready"]);
}
