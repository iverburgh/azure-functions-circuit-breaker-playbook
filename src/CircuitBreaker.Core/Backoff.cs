using System.Text;
using CircuitBreaker.Contracts;

namespace CircuitBreaker.Core;

/// <summary>
/// How long a circuit stays open. The first time it is <see cref="CircuitDefinition.RecoveryTimeout"/>;
/// every failed probe doubles it, up to <see cref="CircuitDefinition.MaxRecoveryTimeout"/>. Jitter
/// spreads circuits that opened at the same moment, so they do not all probe at once.
/// </summary>
public static class RecoveryBackoff
{
    public static TimeSpan Delay(CircuitDefinition definition, int consecutiveReopens, long generation)
    {
        var factor = Math.Pow(2, Math.Clamp(consecutiveReopens, 0, 20));
        var delay = Math.Min(definition.RecoveryTimeout.TotalMilliseconds * factor, definition.MaxRecoveryTimeout.TotalMilliseconds);

        // Deterministic jitter: the same circuit and generation always give the same delay, which keeps tests exact.
        var spread = ((Unit(definition.Key, generation) * 2) - 1) * definition.RecoveryJitter;
        return TimeSpan.FromMilliseconds(delay * (1 + spread));
    }

    private static double Unit(string key, long generation)
    {
        const ulong offset = 14695981039346656037;
        const ulong prime = 1099511628211;
        var hash = offset;
        foreach (var b in Encoding.UTF8.GetBytes($"{key}:{generation}"))
        {
            hash = (hash ^ b) * prime;
        }

        return hash / (double)ulong.MaxValue;
    }
}

/// <summary>Retry schedule for a switch that could not be flipped.</summary>
public static class SwitchRetry
{
    public static TimeSpan Delay(int failedAttempts) =>
        TimeSpan.FromSeconds(Math.Min(15 * Math.Pow(2, Math.Clamp(failedAttempts - 1, 0, 10)), 600));
}
