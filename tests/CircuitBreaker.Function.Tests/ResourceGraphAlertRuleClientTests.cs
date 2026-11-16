using System.Net;
using System.Text;
using System.Text.Json;
using Azure.Core;
using CircuitBreaker.Function.Alerts;

namespace CircuitBreaker.Function.Tests;

public sealed class ResourceGraphAlertRuleClientTests
{
    private const string Rule = "/subscriptions/00000000-0000-0000-0000-000000000001/resourceGroups/rg-monitoring/providers/Microsoft.Insights/scheduledQueryRules/Payments-Errors";

    [Test]
    public async Task The_query_is_sent_with_a_bearer_token_and_the_rows_are_read()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, """
            {"data":[
              {"ruleId":"/subscriptions/x/rule-a","enabled":true,"autoMitigate":true,"firedAlertIds":["/alerts/1"]},
              {"ruleId":"/subscriptions/x/rule-b","enabled":null,"autoMitigate":false,"firedAlertIds":null}
            ]}
            """);
        var client = new ResourceGraphAlertRuleClient(new HttpClient(handler), new FakeCredential());

        var rules = await client.GetRulesAsync([Rule], CancellationToken.None);

        Assert.That(handler.Authorization, Is.EqualTo("Bearer token"));
        Assert.That(handler.Body!.RootElement.GetProperty("options").GetProperty("resultFormat").GetString(), Is.EqualTo("objectArray"));
        Assert.That(rules, Has.Count.EqualTo(2));
        Assert.That(rules[0].FiredAlertIds, Is.EqualTo(new[] { "/alerts/1" }));
        Assert.That(rules[1].Enabled, Is.True, "A rule type without an enabled flag is enabled.");
        Assert.That(rules[1].AutoMitigate, Is.False);
        Assert.That(rules[1].FiredAlertIds, Is.Empty);
    }

    [Test]
    public async Task No_linked_rules_means_no_query()
    {
        var handler = new FakeHandler(HttpStatusCode.OK, "{}");

        var rules = await new ResourceGraphAlertRuleClient(new HttpClient(handler), new FakeCredential()).GetRulesAsync([], CancellationToken.None);

        Assert.That(rules, Is.Empty);
        Assert.That(handler.Calls, Is.Zero);
    }

    [TestCase(HttpStatusCode.TooManyRequests, AlertQueryFailure.Throttled)]
    [TestCase(HttpStatusCode.Unauthorized, AlertQueryFailure.Unauthorized)]
    [TestCase(HttpStatusCode.Forbidden, AlertQueryFailure.Unauthorized)]
    [TestCase(HttpStatusCode.ServiceUnavailable, AlertQueryFailure.ServerError)]
    [TestCase(HttpStatusCode.BadRequest, AlertQueryFailure.InvalidResponse)]
    public void A_failed_response_is_reported_by_kind(HttpStatusCode status, AlertQueryFailure expected)
    {
        var client = new ResourceGraphAlertRuleClient(new HttpClient(new FakeHandler(status, "{}")), new FakeCredential());

        var exception = Assert.ThrowsAsync<AlertQueryException>(() => client.GetRulesAsync([Rule], CancellationToken.None));

        Assert.That(exception!.Kind, Is.EqualTo(expected));
    }

    [Test]
    public void An_unreadable_body_is_an_invalid_response()
    {
        var client = new ResourceGraphAlertRuleClient(new HttpClient(new FakeHandler(HttpStatusCode.OK, "<html>")), new FakeCredential());

        var exception = Assert.ThrowsAsync<AlertQueryException>(() => client.GetRulesAsync([Rule], CancellationToken.None));

        Assert.That(exception!.Kind, Is.EqualTo(AlertQueryFailure.InvalidResponse));
    }

    [Test]
    public void A_token_that_cannot_be_obtained_is_unauthorized()
    {
        var client = new ResourceGraphAlertRuleClient(new HttpClient(new FakeHandler(HttpStatusCode.OK, "{}")), new FakeCredential(fail: true));

        var exception = Assert.ThrowsAsync<AlertQueryException>(() => client.GetRulesAsync([Rule], CancellationToken.None));

        Assert.That(exception!.Kind, Is.EqualTo(AlertQueryFailure.Unauthorized));
    }

    [Test]
    public void The_query_matches_rule_ids_in_lower_case_and_escapes_quotes()
    {
        var query = ResourceGraphAlertRuleClient.BuildQuery([Rule, "/x/it's\\odd"]);

        Assert.That(query, Does.Contain(Rule.ToLowerInvariant()));
        Assert.That(query, Does.Contain(@"'/x/it\'s\\odd'"));
        Assert.That(query, Does.Contain("join kind=leftouter"));
        Assert.That(query, Does.Contain("microsoft.insights/metricalerts"));
    }

    private sealed class FakeHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        public string? Authorization { get; private set; }

        public JsonDocument? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Authorization = request.Headers.Authorization?.ToString();
            Body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    private sealed class FakeCredential(bool fail = false) : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            fail ? throw new InvalidOperationException("No identity.") : new AccessToken("token", DateTimeOffset.MaxValue);

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            new(GetToken(requestContext, cancellationToken));
    }
}
