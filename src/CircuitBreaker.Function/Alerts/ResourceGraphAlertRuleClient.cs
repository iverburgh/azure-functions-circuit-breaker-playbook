using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Azure.Core;
using CircuitBreaker.Core;

namespace CircuitBreaker.Function.Alerts;

/// <summary>
/// Reads alert rules and their firing alerts with one Azure Resource Graph query. The breaker pulls:
/// no action group, webhook or public endpoint is needed, so the function app can stay private.
/// </summary>
/// <remarks>
/// Resource Graph lags behind the real state (tens of seconds), returns only what the identity may read
/// (Monitoring Reader on the rules' resource groups), and throttles per identity.
/// </remarks>
public sealed class ResourceGraphAlertRuleClient(HttpClient http, TokenCredential credential) : IAlertRuleClient
{
    public static readonly Uri Endpoint = new("https://management.azure.com/providers/Microsoft.ResourceGraph/resources?api-version=2022-10-01");

    private static readonly string[] Scopes = ["https://management.azure.com/.default"];

    public async Task<IReadOnlyList<AlertRuleInfo>> GetRulesAsync(IReadOnlyCollection<string> ruleIds, CancellationToken cancellationToken)
    {
        if (ruleIds.Count == 0)
        {
            return [];
        }

        AccessToken token;
        try
        {
            token = await credential.GetTokenAsync(new TokenRequestContext(Scopes), cancellationToken);
        }
        catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
        {
            throw new AlertQueryException(AlertQueryFailure.Timeout, "Timed out while getting a token.", exception);
        }
        catch (Exception exception)
        {
            throw new AlertQueryException(AlertQueryFailure.Unauthorized, "Could not get a token for Azure Resource Manager.", exception);
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = JsonContent.Create(new QueryRequest(BuildQuery(ruleIds), new QueryOptions("objectArray", 1000))),
        };
        request.Headers.Authorization = new("Bearer", token.Token);

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, cancellationToken);
        }
        catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
        {
            throw new AlertQueryException(AlertQueryFailure.Timeout, "The Resource Graph query timed out.", exception);
        }
        catch (HttpRequestException exception)
        {
            throw new AlertQueryException(AlertQueryFailure.Network, $"Resource Graph could not be reached: {exception.Message}", exception);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var kind = response.StatusCode switch
                {
                    HttpStatusCode.TooManyRequests => AlertQueryFailure.Throttled,
                    HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => AlertQueryFailure.Unauthorized,
                    >= HttpStatusCode.InternalServerError => AlertQueryFailure.ServerError,
                    _ => AlertQueryFailure.InvalidResponse,
                };
                throw new AlertQueryException(kind, $"Resource Graph returned {(int)response.StatusCode} {response.StatusCode}.");
            }

            try
            {
                var body = await response.Content.ReadFromJsonAsync<QueryResponse>(cancellationToken);
                return [.. (body?.Data ?? []).Select(row => new AlertRuleInfo(row.RuleId, row.Enabled ?? true, row.AutoMitigate ?? true, row.FiredAlertIds ?? []))];
            }
            catch (JsonException exception)
            {
                throw new AlertQueryException(AlertQueryFailure.InvalidResponse, "Resource Graph returned a response that could not be read.", exception);
            }
        }
    }

    /// <summary>
    /// One query for all rules of a circuit: the rules, joined with their alerts that fire and are not
    /// closed. The IDs are validated at registration; quotes are escaped here as well.
    /// </summary>
    internal static string BuildQuery(IReadOnlyCollection<string> ruleIds)
    {
        var ids = string.Join(", ", ruleIds.Select(id => $"'{id.ToLowerInvariant().Replace("\\", "\\\\").Replace("'", "\\'")}'"));
        var types = string.Join(", ", AlertEvaluator.SupportedTypes.Select(t => $"'{t}'"));
        var query = new StringBuilder()
            .AppendLine("resources")
            .AppendLine($"| where type in~ ({types})")
            .AppendLine($"| where tolower(id) in ({ids})")
            .AppendLine("| project ruleId = tolower(id), enabled = tobool(properties.enabled), autoMitigate = coalesce(tobool(properties.autoMitigate), true)")
            .AppendLine("| join kind=leftouter (")
            .AppendLine("    alertsmanagementresources")
            .AppendLine("    | where type =~ 'microsoft.alertsmanagement/alerts'")
            .AppendLine("    | where properties.essentials.monitorCondition =~ 'Fired' and properties.essentials.alertState !~ 'Closed'")
            .AppendLine("    | summarize firedAlertIds = make_list(id) by ruleId = tolower(tostring(properties.essentials.alertRule))")
            .AppendLine(") on ruleId")
            .Append("| project ruleId, enabled, autoMitigate, firedAlertIds = coalesce(firedAlertIds, dynamic([]))");
        return query.ToString();
    }

    private sealed record QueryRequest(
        [property: JsonPropertyName("query")] string Query,
        [property: JsonPropertyName("options")] QueryOptions Options);

    private sealed record QueryOptions(
        [property: JsonPropertyName("resultFormat")] string ResultFormat,
        [property: JsonPropertyName("$top")] int Top);

    private sealed record QueryResponse([property: JsonPropertyName("data")] List<QueryRow>? Data);

    private sealed record QueryRow(
        [property: JsonPropertyName("ruleId")] string RuleId,
        [property: JsonPropertyName("enabled")] bool? Enabled,
        [property: JsonPropertyName("autoMitigate")] bool? AutoMitigate,
        [property: JsonPropertyName("firedAlertIds")] List<string>? FiredAlertIds);
}
