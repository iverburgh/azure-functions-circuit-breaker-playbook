using System.Net;
using CircuitBreaker.Contracts;

namespace CircuitBreaker.Client.Tests;

public sealed class ClassificationTests
{
    private Fixture _fixture = null!;

    [SetUp]
    public void SetUp()
    {
        _fixture = new Fixture();
        _fixture.Api.SetState(CircuitState.Closed);
    }

    [Test]
    public async Task A_call_that_exceeds_its_attempt_timeout_is_a_timeout_failure()
    {
        var call = _fixture.Breaker.ExecuteAsync(token => Task.Delay(Timeout.Infinite, token));
        _fixture.Time.Advance(TimeSpan.FromSeconds(2));

        Assert.ThrowsAsync<TimeoutException>(() => call);
        var report = await _fixture.FlushSingleReportAsync();
        Assert.That(report.Failures.Single(), Is.EqualTo(new FailureCount(FailureCategory.Timeout, 1)));
    }

    [Test]
    public async Task A_call_its_caller_cancelled_is_not_reported_at_all()
    {
        using var caller = new CancellationTokenSource();
        var call = _fixture.Breaker.ExecuteAsync(token => Task.Delay(Timeout.Infinite, token), caller.Token);
        await caller.CancelAsync();

        Assert.CatchAsync<OperationCanceledException>(() => call);
        await _fixture.Reporter.FlushAsync(CancellationToken.None);
        Assert.That(_fixture.Api.Reports, Is.Empty);
    }

    [Test]
    public async Task Technical_exceptions_count()
    {
        Assert.ThrowsAsync<IOException>(() => _fixture.Breaker.ExecuteAsync(_ => Task.FromException(new IOException("Connection reset"))));

        var report = await _fixture.FlushSingleReportAsync();
        Assert.That(report.Failures.Single().Category, Is.EqualTo(FailureCategory.Exception));
        Assert.That(report.LastFailure!.Message, Is.EqualTo("IOException: Connection reset"));
    }

    [Test]
    public async Task Exceptions_marked_as_functional_are_reported_but_tagged_functional()
    {
        Assert.ThrowsAsync<ArgumentException>(() => _fixture.Breaker.ExecuteAsync(_ => Task.FromException(new ArgumentException("Unknown IBAN"))));

        var report = await _fixture.FlushSingleReportAsync();
        Assert.That(report.Failures.Single().Category, Is.EqualTo(FailureCategory.Functional));
    }

    [TestCase(HttpStatusCode.OK, null)]
    [TestCase(HttpStatusCode.NotFound, FailureCategory.Functional)]
    [TestCase(HttpStatusCode.Conflict, FailureCategory.Functional)]
    [TestCase(HttpStatusCode.RequestTimeout, FailureCategory.Timeout)]
    [TestCase(HttpStatusCode.TooManyRequests, FailureCategory.Throttled)]
    [TestCase(HttpStatusCode.InternalServerError, FailureCategory.ServerError)]
    [TestCase(HttpStatusCode.ServiceUnavailable, FailureCategory.ServerError)]
    public void Http_status_codes_map_to_categories(HttpStatusCode status, FailureCategory? expected)
    {
        var outcome = HttpOutcome.FromResponse(new HttpResponseMessage(status));

        Assert.That(outcome.Category, Is.EqualTo(expected));
        Assert.That(outcome.Kind, Is.EqualTo(expected is null ? OutcomeKind.Success : OutcomeKind.Failure));
    }

    [Test]
    public void A_503_with_retry_after_is_throttling()
    {
        var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(10));

        Assert.That(HttpOutcome.FromResponse(response).Category, Is.EqualTo(FailureCategory.Throttled));
    }

    [Test]
    public async Task A_returned_error_response_is_judged_and_still_returned()
    {
        using var response = await _fixture.Breaker.ExecuteAsync(
            _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway)),
            HttpOutcome.FromResponse);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadGateway));
        var report = await _fixture.FlushSingleReportAsync();
        Assert.That(report.Failures.Single().Category, Is.EqualTo(FailureCategory.ServerError));
    }

    [Test]
    public async Task An_unreported_permit_releases_its_probe_without_a_verdict()
    {
        _fixture.Api.SetState(CircuitState.HalfOpen);
        var probeId = Guid.NewGuid();
        _fixture.Api.NextPermit = new ProbePermit(true, CircuitState.HalfOpen, probeId);

        using (await _fixture.Breaker.AcquireAsync())
        {
            // The caller decided not to call after all.
        }

        var report = await _fixture.FlushSingleReportAsync();
        Assert.That(report.ProbeResults.Single(), Is.EqualTo(new ProbeResult(probeId, false, null)));
    }
}
