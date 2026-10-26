using System.Text.Json;
using CircuitBreaker.Contracts;
using CircuitBreaker.Core;
using CircuitBreaker.Function.Entities;
using CircuitBreaker.Function.Orchestrations;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.DurableTask.Client;
using Microsoft.DurableTask.Client.Entities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CircuitBreaker.Function.Http;

public sealed class ApiOptions
{
    /// <summary>How long a probe request waits for the entity's answer before it is treated as denied. Keep it below the client's BreakerCallTimeout.</summary>
    public TimeSpan ProbeWaitTimeout { get; set; } = TimeSpan.FromSeconds(2);

    public TimeSpan ManualActionWaitTimeout { get; set; } = TimeSpan.FromSeconds(20);
}

/// <summary>
/// The HTTP surface. Writes that need no answer (register, report) are signals: the request returns
/// as soon as the operation is queued, and the entity applies them in order. Reads come straight from
/// the persisted entity state.
/// </summary>
public sealed class CircuitApi(RequestAuthorizer authorizer, IOptions<ApiOptions> options, TimeProvider timeProvider, ILogger<CircuitApi> logger)
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Function("RegisterCircuit")]
    public async Task<IActionResult> Register(
        [HttpTrigger(AuthorizationLevel.Anonymous, "put", Route = "circuits/{key}")] HttpRequest request,
        string key,
        [DurableClient] DurableTaskClient client)
    {
        if (authorizer.Require(request, Roles.Reporter) is { } denied)
        {
            return denied;
        }

        var definition = await ReadAsync<CircuitDefinition>(request);
        if (definition is null)
        {
            return Problem("The body must be a circuit definition.");
        }

        var errors = definition.Validate().ToList();
        if (definition.Key != key)
        {
            errors.Add("The key in the body must match the key in the URL.");
        }

        if (errors.Count > 0)
        {
            return Problem(string.Join(" ", errors));
        }

        await client.Entities.SignalEntityAsync(CircuitEntity.IdFor(key), CircuitOperations.Register, definition, cancellation: request.HttpContext.RequestAborted);
        return new AcceptedResult($"/api/circuits/{key}", null);
    }

    [Function("ReportOutcomes")]
    public async Task<IActionResult> ReportOutcomes(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "circuits/{key}/outcomes")] HttpRequest request,
        string key,
        [DurableClient] DurableTaskClient client)
    {
        if (authorizer.Require(request, Roles.Reporter) is { } denied)
        {
            return denied;
        }

        var report = await ReadAsync<OutcomeReport>(request);
        if (report is null)
        {
            return Problem("The body must be an outcome report.");
        }

        if (!report.IsEmpty)
        {
            await client.Entities.SignalEntityAsync(CircuitEntity.IdFor(key), CircuitOperations.ReportOutcomes, report, cancellation: request.HttpContext.RequestAborted);
        }

        return new AcceptedResult();
    }

    [Function("RequestProbe")]
    public async Task<IActionResult> RequestProbe(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "circuits/{key}/probes")] HttpRequest request,
        string key,
        [DurableClient] DurableTaskClient client)
    {
        if (authorizer.Require(request, Roles.Reporter) is { } denied)
        {
            return denied;
        }

        var probeRequest = await ReadAsync<ProbeRequest>(request) ?? new ProbeRequest();
        var instanceId = await client.ScheduleNewOrchestrationInstanceAsync(
            nameof(CircuitOrchestrations.AcquireProbeOrchestrator), new ProbeOrchestrationInput(key, probeRequest), request.HttpContext.RequestAborted);

        var permit = await WaitForOutputAsync<ProbePermit>(client, instanceId, options.Value.ProbeWaitTimeout, request.HttpContext.RequestAborted);

        // No answer in time: deny. A permit granted late is never used; its lease expires and frees the slot.
        return new OkObjectResult(permit ?? new ProbePermit(false, CircuitState.HalfOpen, RetryAfter: timeProvider.GetUtcNow().AddSeconds(1)));
    }

    [Function("GetCircuit")]
    public async Task<IActionResult> Get(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "circuits/{key}")] HttpRequest request,
        string key,
        [DurableClient] DurableTaskClient client)
    {
        if (authorizer.Require(request, Roles.Reporter, Roles.Reader, Roles.Operator) is { } denied)
        {
            return denied;
        }

        var entity = await client.Entities.GetEntityAsync<CircuitData>(CircuitEntity.IdFor(key), request.HttpContext.RequestAborted);
        return entity?.State.Definition is null
            ? new NotFoundResult()
            : new OkObjectResult(CircuitEngine.ToSnapshot(entity.State, timeProvider.GetUtcNow()));
    }

    [Function("ListCircuits")]
    public async Task<IActionResult> List(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "circuits")] HttpRequest request,
        [DurableClient] DurableTaskClient client)
    {
        if (authorizer.Require(request, Roles.Reader, Roles.Operator) is { } denied)
        {
            return denied;
        }

        var now = timeProvider.GetUtcNow();
        var snapshots = new List<CircuitSnapshot>();
        var query = new EntityQuery { InstanceIdStartsWith = CircuitEntity.Name, IncludeState = true };
        await foreach (var entity in client.Entities.GetAllEntitiesAsync<CircuitData>(query).WithCancellation(request.HttpContext.RequestAborted))
        {
            if (entity.State?.Definition is not null)
            {
                snapshots.Add(CircuitEngine.ToSnapshot(entity.State, now));
            }
        }

        return new OkObjectResult(snapshots.OrderBy(s => s.Key, StringComparer.Ordinal).ToList());
    }

    [Function("ApplyCircuitAction")]
    public async Task<IActionResult> ApplyAction(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "circuits/{key}/actions/{action}")] HttpRequest request,
        string key,
        string action,
        [DurableClient] DurableTaskClient client)
    {
        if (authorizer.Require(request, Roles.Operator) is { } denied)
        {
            return denied;
        }

        if (!Enum.TryParse<CircuitAction>(action, ignoreCase: true, out var circuitAction) || !Enum.IsDefined(circuitAction))
        {
            return Problem($"Unknown action '{action}'. Use one of: {string.Join(", ", Enum.GetNames<CircuitAction>())}.");
        }

        var body = await ReadAsync<ManualActionRequest>(request);
        if (body is null || string.IsNullOrWhiteSpace(body.Reason) || body.Reason.Trim().Length < 3)
        {
            return Problem("A reason of at least 3 characters is required; it is recorded in the circuit's history.");
        }

        var existing = await client.Entities.GetEntityAsync<CircuitData>(CircuitEntity.IdFor(key), includeState: true, request.HttpContext.RequestAborted);
        if (existing?.State.Definition is null)
        {
            return new NotFoundResult();
        }

        var actor = authorizer.GetCaller(request)?.Name ?? "unknown";
        var instanceId = await client.ScheduleNewOrchestrationInstanceAsync(
            nameof(CircuitOrchestrations.ManualActionOrchestrator),
            new ManualActionOrchestrationInput(key, new ManualActionCommand(circuitAction, body.Reason.Trim(), actor)),
            request.HttpContext.RequestAborted);

        var result = await WaitForOutputAsync<ManualActionResult>(client, instanceId, options.Value.ManualActionWaitTimeout, request.HttpContext.RequestAborted);
        if (result is null)
        {
            return new ObjectResult(new ProblemDetails { Title = "The action is queued but did not complete in time. Refresh to see the result." })
            {
                StatusCode = StatusCodes.Status202Accepted,
            };
        }

        return result.Accepted ? new OkObjectResult(result) : new ConflictObjectResult(result);
    }

    [Function("WhoAmI")]
    public IActionResult WhoAmI([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "me")] HttpRequest request) =>
        authorizer.GetCaller(request) is { } caller ? new OkObjectResult(caller) : new UnauthorizedResult();

    private async Task<T?> WaitForOutputAsync<T>(DurableTaskClient client, string instanceId, TimeSpan timeout, CancellationToken cancellationToken)
        where T : class
    {
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        wait.CancelAfter(timeout);
        try
        {
            var instance = await client.WaitForInstanceCompletionAsync(instanceId, getInputsAndOutputs: true, wait.Token);
            if (instance.RuntimeStatus != OrchestrationRuntimeStatus.Completed)
            {
                logger.LogError("Orchestration {InstanceId} ended as {Status}: {Failure}", instanceId, instance.RuntimeStatus, instance.FailureDetails?.ErrorMessage);
                return null;
            }

            var output = instance.ReadOutputAs<T>();
            await client.PurgeInstanceAsync(instanceId, cancellation: CancellationToken.None);
            return output;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Orchestration {InstanceId} did not complete within {Timeout}", instanceId, timeout);
            return null;
        }
    }

    private static async Task<T?> ReadAsync<T>(HttpRequest request)
        where T : class
    {
        try
        {
            return await request.ReadFromJsonAsync<T>(Json, request.HttpContext.RequestAborted);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    private static BadRequestObjectResult Problem(string detail) =>
        new(new ProblemDetails { Title = "Invalid request", Detail = detail, Status = StatusCodes.Status400BadRequest });
}
