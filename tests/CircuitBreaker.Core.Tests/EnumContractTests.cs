using CircuitBreaker.Contracts;

namespace CircuitBreaker.Core.Tests;

/// <summary>
/// States, triggers and the other enums travel as names in snapshots, history and stored entity
/// state. A reader that does not know a name cannot read the message, so names are append-only:
/// never rename, remove or reorder; add new names at the end and deploy every reader first.
/// When you add a name, add it at the end of the list below as well.
/// </summary>
public sealed class EnumContractTests
{
    private static readonly object[] Pinned =
    [
        new object[] { typeof(CircuitState), new[] { "Closed", "Open", "HalfOpen", "Isolated", "Disabled", "OpenedByAlert", "OpenedAfterAlert" } },
        new object[] { typeof(CircuitTrigger), new[] { "FailureThresholdReached", "RecoveryTimeoutElapsed", "ProbesSucceeded", "ProbeFailed", "Isolate", "Disable", "Reset", "AlertsActive", "AlertsCleared" } },
        new object[] { typeof(FailureCategory), new[] { "Timeout", "Exception", "ServerError", "Throttled", "Functional" } },
        new object[] { typeof(CircuitAction), new[] { "Isolate", "Disable", "Reset", "Delete" } },
        new object[] { typeof(SwitchKind), new[] { "FeatureFlag", "ServiceBusSubscription" } },
        new object[] { typeof(SwitchPosition), new[] { "Closed", "Open" } },
        new object[] { typeof(AlertPollingMode), new[] { "Off", "Shadow", "Active" } },
        new object[] { typeof(AlertRuleSuitability), new[] { "Suitable", "UnsupportedType", "NotFound", "Stateless", "Disabled" } },
        new object[] { typeof(CircuitHealthStatus), new[] { "Healthy", "Degraded", "Unhealthy" } },
    ];

    [TestCaseSource(nameof(Pinned))]
    public void Enum_names_are_append_only(Type type, string[] pinned)
    {
        var names = Enum.GetNames(type);

        Assert.That(names.Take(pinned.Length), Is.EqualTo(pinned), $"{type.Name}: a pinned name was renamed, removed or moved.");
        Assert.That(names.Skip(pinned.Length), Is.Empty, $"{type.Name}: pin the new name(s) at the end of the list in this test.");
    }
}
