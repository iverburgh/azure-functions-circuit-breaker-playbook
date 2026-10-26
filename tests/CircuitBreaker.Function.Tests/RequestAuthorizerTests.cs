using System.Text;
using System.Text.Json;
using CircuitBreaker.Function.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace CircuitBreaker.Function.Tests;

public sealed class RequestAuthorizerTests
{
    private static RequestAuthorizer EasyAuth() => new(Options.Create(new SecurityOptions { Mode = SecurityMode.EasyAuth }));

    private static HttpRequest RequestWith(params (string Type, string Value)[] claims)
    {
        var principal = new
        {
            auth_typ = "aad",
            name_typ = "name",
            role_typ = "http://schemas.microsoft.com/ws/2008/06/identity/claims/role",
            claims = claims.Select(c => new { typ = c.Type, val = c.Value }),
        };
        var context = new DefaultHttpContext();
        context.Request.Headers["X-MS-CLIENT-PRINCIPAL"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(principal)));
        return context.Request;
    }

    [Test]
    public void Roles_come_from_the_roles_claim_of_the_validated_principal()
    {
        var caller = EasyAuth().GetCaller(RequestWith(("name", "Alice"), ("roles", Roles.Operator)));

        Assert.That(caller!.Name, Is.EqualTo("Alice"));
        Assert.That(caller.Roles, Is.EqualTo(new[] { Roles.Operator }));
    }

    [Test]
    public void A_service_principal_is_named_by_its_client_id()
    {
        var caller = EasyAuth().GetCaller(RequestWith(("azp", "orders-service"), ("roles", Roles.Reporter)));

        Assert.That(caller!.Name, Is.EqualTo("orders-service"));
    }

    [Test]
    public void Without_a_principal_the_request_is_unauthorized() =>
        Assert.That(EasyAuth().Require(new DefaultHttpContext().Request, Roles.Reader), Is.InstanceOf<UnauthorizedResult>());

    [Test]
    public void A_garbled_principal_header_is_unauthorized()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["X-MS-CLIENT-PRINCIPAL"] = "not base64!";

        Assert.That(EasyAuth().Require(context.Request, Roles.Reader), Is.InstanceOf<UnauthorizedResult>());
    }

    [Test]
    public void A_caller_without_the_role_is_forbidden()
    {
        var result = EasyAuth().Require(RequestWith(("roles", Roles.Reader)), Roles.Operator);

        Assert.That(result, Is.InstanceOf<StatusCodeResult>().With.Property(nameof(StatusCodeResult.StatusCode)).EqualTo(403));
    }

    [Test]
    public void A_caller_with_any_of_the_roles_is_allowed() =>
        Assert.That(EasyAuth().Require(RequestWith(("roles", Roles.Reader)), Roles.Reader, Roles.Operator), Is.Null);

    [Test]
    public void Development_mode_grants_the_configured_roles_to_everyone()
    {
        var authorizer = new RequestAuthorizer(Options.Create(new SecurityOptions { Mode = SecurityMode.Development, DevelopmentRoles = [Roles.Reader] }));

        Assert.That(authorizer.Require(new DefaultHttpContext().Request, Roles.Reader), Is.Null);
        Assert.That(authorizer.Require(new DefaultHttpContext().Request, Roles.Operator), Is.Not.Null);
    }
}
