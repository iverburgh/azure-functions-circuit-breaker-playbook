using CircuitBreaker.Contracts;
using CircuitBreaker.Core;
using CircuitBreaker.Function.Entities;
using Microsoft.Azure.Functions.Worker;
using Microsoft.DurableTask;

namespace CircuitBreaker.Function.Orchestrations;

public sealed record ProbeOrchestrationInput(string CircuitKey, ProbeRequest Request);

public sealed record ManualActionOrchestrationInput(string CircuitKey, ManualActionCommand Command);

/// <summary>
/// A client can signal an entity, but not wait for its answer. These one-step orchestrations call the
/// entity and return its answer, for the two operations that need one: handing out a probe permit
/// (atomically, so concurrent callers never exceed the limit) and applying a manual action.
/// They only run while half-open or when an operator acts, so their latency does not matter.
/// </summary>
public static class CircuitOrchestrations
{
    [Function(nameof(AcquireProbeOrchestrator))]
    public static Task<ProbePermit> AcquireProbeOrchestrator([OrchestrationTrigger] TaskOrchestrationContext context)
    {
        var input = context.GetInput<ProbeOrchestrationInput>()
            ?? throw new InvalidOperationException("Missing orchestration input.");
        return context.Entities.CallEntityAsync<ProbePermit>(CircuitEntity.IdFor(input.CircuitKey), CircuitOperations.TryAcquireProbe, input.Request);
    }

    [Function(nameof(ManualActionOrchestrator))]
    public static Task<ManualActionResult> ManualActionOrchestrator([OrchestrationTrigger] TaskOrchestrationContext context)
    {
        var input = context.GetInput<ManualActionOrchestrationInput>()
            ?? throw new InvalidOperationException("Missing orchestration input.");
        return context.Entities.CallEntityAsync<ManualActionResult>(CircuitEntity.IdFor(input.CircuitKey), CircuitOperations.ApplyManualAction, input.Command);
    }
}
