using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CircuitBreaker.Contracts;

namespace CircuitBreaker.Client;

/// <summary>The breaker's HTTP API, as the client uses it.</summary>
public interface ICircuitBreakerApi
{
    Task RegisterAsync(CircuitDefinition definition, CancellationToken cancellationToken);

    /// <summary>Returns <c>null</c> when the breaker does not know the circuit.</summary>
    Task<CircuitSnapshot?> GetAsync(string circuitKey, CancellationToken cancellationToken);

    Task<ProbePermit> RequestProbeAsync(string circuitKey, ProbeRequest request, CancellationToken cancellationToken);

    Task ReportAsync(string circuitKey, OutcomeReport report, CancellationToken cancellationToken);
}

/// <summary>Creates an <see cref="HttpClient"/> per call through <see cref="IHttpClientFactory"/>, so handlers rotate normally.</summary>
internal sealed class CircuitBreakerApiClient(Func<HttpClient> createClient) : ICircuitBreakerApi
{
    public const string HttpClientName = "CircuitBreakerApi";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task RegisterAsync(CircuitDefinition definition, CancellationToken cancellationToken)
    {
        using var response = await createClient().PutAsJsonAsync($"api/circuits/{definition.Key}", definition, Json, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task<CircuitSnapshot?> GetAsync(string circuitKey, CancellationToken cancellationToken)
    {
        using var response = await createClient().GetAsync($"api/circuits/{circuitKey}", cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<CircuitSnapshot>(Json, cancellationToken);
    }

    public async Task<ProbePermit> RequestProbeAsync(string circuitKey, ProbeRequest request, CancellationToken cancellationToken)
    {
        using var response = await createClient().PostAsJsonAsync($"api/circuits/{circuitKey}/probes", request, Json, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ProbePermit>(Json, cancellationToken)
            ?? throw new InvalidOperationException("The breaker returned an empty probe permit.");
    }

    public async Task ReportAsync(string circuitKey, OutcomeReport report, CancellationToken cancellationToken)
    {
        using var response = await createClient().PostAsJsonAsync($"api/circuits/{circuitKey}/outcomes", report, Json, cancellationToken);
        response.EnsureSuccessStatusCode();
    }
}

/// <summary>Adds a bearer token to calls to the breaker. Supply the token, for example from Azure.Identity.</summary>
public sealed class BearerTokenHandler(Func<CancellationToken, ValueTask<string>> getToken) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", await getToken(cancellationToken));
        return await base.SendAsync(request, cancellationToken);
    }
}
