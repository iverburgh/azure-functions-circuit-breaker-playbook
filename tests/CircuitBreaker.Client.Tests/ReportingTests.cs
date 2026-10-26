using System.Net;
using CircuitBreaker.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Polly;

namespace CircuitBreaker.Client.Tests;

public sealed class ReportingTests
{
    [Test]
    public async Task Outcomes_are_aggregated_into_one_report_per_circuit()
    {
        var fixture = new Fixture();
        fixture.Api.SetState(CircuitState.Closed);
        for (var i = 0; i < 100; i++)
        {
            await fixture.Breaker.ExecuteAsync(_ => Task.CompletedTask);
        }

        for (var i = 0; i < 3; i++)
        {
            Assert.ThrowsAsync<IOException>(() => fixture.Breaker.ExecuteAsync(_ => Task.FromException(new IOException("reset"))));
        }

        var report = await fixture.FlushSingleReportAsync();
        Assert.That(report.Successes, Is.EqualTo(100));
        Assert.That(report.Failures.Single(), Is.EqualTo(new FailureCount(FailureCategory.Exception, 3)));
        Assert.That(report.Source, Is.EqualTo("test-instance"));
    }

    [Test]
    public async Task A_report_that_could_not_be_sent_is_kept_and_merged_with_newer_outcomes()
    {
        var fixture = new Fixture();
        fixture.Reporter.Record("payments-api", Outcome.Success, null);
        fixture.Api.FailReports = 1;
        await fixture.Reporter.FlushAsync(CancellationToken.None);
        Assert.That(fixture.Api.Reports, Is.Empty);

        fixture.Reporter.Record("payments-api", Outcome.Failure(FailureCategory.Timeout), null);
        var report = await fixture.FlushSingleReportAsync();

        Assert.That(report.Successes, Is.EqualTo(1));
        Assert.That(report.Failures.Single(), Is.EqualTo(new FailureCount(FailureCategory.Timeout, 1)));
    }

    [Test]
    public async Task Outcomes_too_old_to_count_are_dropped_rather_than_reported_late()
    {
        var fixture = new Fixture();
        fixture.Reporter.Record("payments-api", Outcome.Failure(FailureCategory.Timeout), null);
        fixture.Api.Unreachable = true;
        await fixture.Reporter.FlushAsync(CancellationToken.None);

        fixture.Time.Advance(TimeSpan.FromSeconds(31));
        fixture.Api.Unreachable = false;
        await fixture.Reporter.FlushAsync(CancellationToken.None);

        Assert.That(fixture.Api.Reports, Is.Empty);
    }

    [Test]
    public async Task Registration_retries_until_every_circuit_is_known()
    {
        var api = new FakeBreakerApi();
        api.FailRegistrationFor.Add("orders-api");
        var circuits = new[]
        {
            new CircuitOptions { Definition = Fixture.Definition },
            new CircuitOptions { Definition = Fixture.Definition with { Key = "orders-api", DisplayName = "Orders API" } },
        };
        var service = new CircuitRegistrationService(circuits, api, new RegistrationSignal(),
            Microsoft.Extensions.Options.Options.Create(new CircuitBreakerClientOptions()), new FakeTimeProvider(Fixture.T0), NullLogger<CircuitRegistrationService>.Instance);

        Assert.That(await service.RegisterAllAsync(CancellationToken.None), Is.False);
        api.FailRegistrationFor.Clear();
        Assert.That(await service.RegisterAllAsync(CancellationToken.None), Is.True);
        Assert.That(api.Registered.Select(d => d.Key), Is.EquivalentTo(new[] { "payments-api", "orders-api", "payments-api" }));
    }
}

public sealed class RetryCompositionTests
{
    private ServiceProvider _services = null!;
    private FakeBreakerApi _api = null!;
    private SequenceHandler _downstream = null!;

    [SetUp]
    public void SetUp()
    {
        _api = new FakeBreakerApi();
        _downstream = new SequenceHandler();
        var services = new ServiceCollection();
        services.AddSingleton<Microsoft.Extensions.Configuration.IConfiguration>(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
        services.AddSingleton<TimeProvider>(new FakeTimeProvider(Fixture.T0));
        services.AddSingleton<ICircuitBreakerApi>(_api);
        services.AddLogging();
        services.AddCircuitBreakerClient(options => options.ServiceUri = new Uri("https://breaker.example.com/"))
            .AddCircuit(new CircuitOptions { Definition = Fixture.Definition });

        // Order matters: the retry is outside the circuit, so every attempt is judged, and a refusal is not retried.
        var payments = services.AddHttpClient("payments", client => client.BaseAddress = new Uri("https://payments.example.com/"));
        payments.AddResilienceHandler("retry", pipeline => pipeline.AddRetry(new HttpRetryStrategyOptions
        {
            MaxRetryAttempts = 2,
            Delay = TimeSpan.Zero,
            ShouldHandle = args => ValueTask.FromResult(args.Outcome switch
            {
                { Exception: BrokenCircuitException } => false,
                { Exception: not null } => true,
                { Result: { } response } => (int)response.StatusCode >= 500,
                _ => false,
            }),
        }));
        payments.AddCircuitBreaker("payments-api");
        payments.ConfigurePrimaryHttpMessageHandler(() => _downstream);

        _services = services.BuildServiceProvider();
    }

    [TearDown]
    public void TearDown() => _services.Dispose();

    [Test]
    public async Task Each_retry_attempt_is_reported_as_its_own_outcome()
    {
        _api.SetState(CircuitState.Closed);
        _downstream.Responses.Enqueue(HttpStatusCode.ServiceUnavailable);
        _downstream.Responses.Enqueue(HttpStatusCode.ServiceUnavailable);
        _downstream.Responses.Enqueue(HttpStatusCode.OK);

        using var response = await _services.GetRequiredService<IHttpClientFactory>().CreateClient("payments").GetAsync("pay");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        await _services.GetRequiredService<OutcomeReporter>().FlushAsync(CancellationToken.None);
        var report = _api.Reports.Single().Report;
        Assert.That(report.Failures.Single(), Is.EqualTo(new FailureCount(FailureCategory.ServerError, 2)));
        Assert.That(report.Successes, Is.EqualTo(1));
    }

    [Test]
    public void An_open_circuit_is_not_retried_and_the_dependency_is_never_called()
    {
        _api.SetState(CircuitState.Open, Fixture.T0.AddMinutes(1));

        Assert.ThrowsAsync<BrokenCircuitException>(() => _services.GetRequiredService<IHttpClientFactory>().CreateClient("payments").GetAsync("pay"));

        Assert.That(_downstream.Calls, Is.Zero);
        Assert.That(_api.Gets, Is.EqualTo(1), "One refusal, no retry storm against the breaker either.");
    }

    [Test]
    public void An_invalid_circuit_definition_fails_at_start_up()
    {
        var services = new ServiceCollection();

        Assert.Throws<ArgumentException>(() => services.AddCircuitBreakerClient()
            .AddCircuit(new CircuitOptions { Definition = Fixture.Definition with { Key = "Not Valid" } }));
    }

    private sealed class SequenceHandler : HttpMessageHandler
    {
        public Queue<HttpStatusCode> Responses { get; } = new();

        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(Responses.Count > 0 ? Responses.Dequeue() : HttpStatusCode.OK) { RequestMessage = request });
        }
    }
}
