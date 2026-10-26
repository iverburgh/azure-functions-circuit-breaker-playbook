using System.Net;
using CircuitBreaker.Contracts;

namespace CircuitBreaker.Client;

public enum OutcomeKind
{
    Success,
    Failure,

    /// <summary>Says nothing about the dependency's health, such as a call cancelled by its caller. Not reported.</summary>
    Ignored,
}

/// <summary>How one call went, as far as the circuit is concerned.</summary>
public readonly record struct Outcome(OutcomeKind Kind, FailureCategory? Category = null, string? Message = null)
{
    public static Outcome Success { get; } = new(OutcomeKind.Success);

    public static Outcome Ignored { get; } = new(OutcomeKind.Ignored);

    public static Outcome Failure(FailureCategory category, string? message = null) => new(OutcomeKind.Failure, category, message);
}

/// <summary>Decides what a failed call means. Replace it to recognise your own functional exceptions.</summary>
public interface IFailureClassifier
{
    /// <param name="exception">What the call threw.</param>
    /// <param name="attemptTimedOut">True when the circuit's own attempt timeout cancelled the call.</param>
    /// <param name="callerCancelled">True when the caller's token cancelled the call.</param>
    Outcome Classify(Exception exception, bool attemptTimedOut, bool callerCancelled);
}

/// <summary>
/// Sensible defaults: timeouts, technical exceptions, 5xx and 429 count; 4xx and the exception types
/// you mark as functional do not; a caller giving up is ignored altogether.
/// </summary>
public class DefaultFailureClassifier(params Type[] functionalExceptions) : IFailureClassifier
{
    public virtual Outcome Classify(Exception exception, bool attemptTimedOut, bool callerCancelled)
    {
        if (attemptTimedOut)
        {
            return Outcome.Failure(FailureCategory.Timeout, "The call exceeded its attempt timeout.");
        }

        if (callerCancelled && exception is OperationCanceledException)
        {
            return Outcome.Ignored;
        }

        if (functionalExceptions.Any(type => type.IsInstanceOfType(exception)))
        {
            return Outcome.Failure(FailureCategory.Functional, exception.Message);
        }

        return exception switch
        {
            TimeoutException => Outcome.Failure(FailureCategory.Timeout, exception.Message),
            HttpRequestException { StatusCode: { } status } => HttpOutcome.FromStatus(status, retryAfter: false) with { Message = exception.Message },
            _ => Outcome.Failure(FailureCategory.Exception, $"{exception.GetType().Name}: {exception.Message}"),
        };
    }
}

public static class HttpOutcome
{
    public static Outcome FromResponse(HttpResponseMessage response) =>
        FromStatus(response.StatusCode, response.Headers.RetryAfter is not null) is { Kind: OutcomeKind.Failure } failure
            ? failure with { Message = $"{(int)response.StatusCode} {response.ReasonPhrase} from {response.RequestMessage?.RequestUri?.Host}" }
            : Outcome.Success;

    public static Outcome FromStatus(HttpStatusCode status, bool retryAfter) => (int)status switch
    {
        < 400 => Outcome.Success,
        408 => Outcome.Failure(FailureCategory.Timeout),
        429 => Outcome.Failure(FailureCategory.Throttled),
        503 when retryAfter => Outcome.Failure(FailureCategory.Throttled),
        >= 500 => Outcome.Failure(FailureCategory.ServerError),
        _ => Outcome.Failure(FailureCategory.Functional),
    };
}
