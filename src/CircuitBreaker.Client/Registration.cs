using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CircuitBreaker.Client;

/// <summary>Wakes the registration service early, for example when the breaker turns out not to know a circuit.</summary>
internal sealed class RegistrationSignal
{
    private readonly Channel<bool> _channel = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    public void Request() => _channel.Writer.TryWrite(true);

    public async Task WaitAsync(TimeSpan timeout, TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        await Task.WhenAny(Task.Delay(timeout, timeProvider, wait.Token), _channel.Reader.ReadAsync(wait.Token).AsTask());
        await wait.CancelAsync();
    }
}

/// <summary>Registers every circuit on start-up, retries until it works, and refreshes periodically.</summary>
internal sealed partial class CircuitRegistrationService(
    IEnumerable<CircuitOptions> circuits,
    ICircuitBreakerApi api,
    RegistrationSignal signal,
    IOptions<CircuitBreakerClientOptions> options,
    TimeProvider timeProvider,
    ILogger<CircuitRegistrationService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var allRegistered = await RegisterAllAsync(stoppingToken);
            var wait = allRegistered ? options.Value.RegistrationRefreshInterval : options.Value.RegistrationRetryInterval;
            await signal.WaitAsync(wait, timeProvider, stoppingToken);
        }
    }

    internal async Task<bool> RegisterAllAsync(CancellationToken cancellationToken)
    {
        var results = await Task.WhenAll(circuits.Select(async circuit =>
        {
            try
            {
                await api.RegisterAsync(circuit.Definition, cancellationToken);
                return true;
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                LogRegistrationFailed(exception, circuit.Definition.Key);
                return false;
            }
        }));

        return results.All(registered => registered);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not register circuit {CircuitKey}; retrying later. Calls are allowed meanwhile")]
    private partial void LogRegistrationFailed(Exception exception, string circuitKey);
}

public interface ICircuitBreakerBuilder
{
    IServiceCollection Services { get; }

    /// <summary>The HTTP client for the breaker's API. Add your authentication handler here.</summary>
    IHttpClientBuilder ApiHttpClient { get; }

    ICircuitBreakerBuilder AddCircuit(CircuitOptions circuit);
}

public static class CircuitBreakerServiceCollectionExtensions
{
    /// <summary>Adds the client. Settings bind from the <c>CircuitBreaker</c> configuration section.</summary>
    public static ICircuitBreakerBuilder AddCircuitBreakerClient(this IServiceCollection services, Action<CircuitBreakerClientOptions>? configure = null)
    {
        var optionsBuilder = services.AddOptions<CircuitBreakerClientOptions>().BindConfiguration("CircuitBreaker");
        if (configure is not null)
        {
            optionsBuilder.Configure(configure);
        }

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<RegistrationSignal>();
        services.TryAddSingleton<OutcomeReporter>();
        services.TryAddSingleton<ICircuitBreakerApi>(sp =>
        {
            var factory = sp.GetRequiredService<IHttpClientFactory>();
            return new CircuitBreakerApiClient(() => factory.CreateClient(CircuitBreakerApiClient.HttpClientName));
        });
        services.AddHostedService(sp => sp.GetRequiredService<OutcomeReporter>());
        services.AddHostedService<CircuitRegistrationService>();

        var http = services.AddHttpClient(CircuitBreakerApiClient.HttpClientName, (sp, client) =>
        {
            var options = sp.GetRequiredService<IOptions<CircuitBreakerClientOptions>>().Value;
            client.BaseAddress = options.ServiceUri ?? throw new InvalidOperationException("CircuitBreaker:ServiceUri is not configured.");

            // Each call sets its own short timeout; the client-wide one would only hide that.
            client.Timeout = Timeout.InfiniteTimeSpan;
        });

        return new Builder(services, http);
    }

    /// <summary>Guards every request of this HTTP client with the circuit. Add it after retries, so each attempt counts.</summary>
    public static IHttpClientBuilder AddCircuitBreaker(this IHttpClientBuilder builder, string circuitKey) =>
        builder.AddHttpMessageHandler(sp => new CircuitBreakerHandler(sp.GetRequiredKeyedService<ICircuitBreaker>(circuitKey)));

    private sealed class Builder(IServiceCollection services, IHttpClientBuilder apiHttpClient) : ICircuitBreakerBuilder
    {
        public IServiceCollection Services => services;

        public IHttpClientBuilder ApiHttpClient => apiHttpClient;

        public ICircuitBreakerBuilder AddCircuit(CircuitOptions circuit)
        {
            var errors = circuit.Definition.Validate();
            if (errors.Count > 0)
            {
                throw new ArgumentException($"Circuit '{circuit.Definition.Key}' is invalid: {string.Join(" ", errors)}", nameof(circuit));
            }

            services.AddSingleton(circuit);
            services.AddKeyedSingleton<ICircuitBreaker>(circuit.Definition.Key, (sp, _) => ActivatorUtilities.CreateInstance<CircuitBreakerGate>(sp, circuit));
            return this;
        }
    }
}

/// <summary>Runs each request through the circuit, and judges the response by its status code.</summary>
public sealed class CircuitBreakerHandler(ICircuitBreaker circuitBreaker) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        circuitBreaker.ExecuteAsync(token => base.SendAsync(request, token), HttpOutcome.FromResponse, cancellationToken);
}
