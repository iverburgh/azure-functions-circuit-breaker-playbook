using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CircuitBreaker.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace CircuitBreaker.Function.Http;

/// <summary>App roles, defined on the API's app registration and assigned to services and people.</summary>
public static class Roles
{
    /// <summary>Services that call through circuits: register, report outcomes, request probes, read their circuit.</summary>
    public const string Reporter = "CircuitBreaker.Reporter";

    /// <summary>People who may look at circuits.</summary>
    public const string Reader = "CircuitBreaker.Reader";

    /// <summary>People who may isolate, disable, reset and delete circuits.</summary>
    public const string Operator = "CircuitBreaker.Operator";

    public static readonly string[] All = [Reporter, Reader, Operator];
}

public enum SecurityMode
{
    /// <summary>Trust the identity that App Service Authentication (Easy Auth) validated and passes on.</summary>
    EasyAuth,

    /// <summary>Local development only: every request is a fixed user with all roles. Refused outside Development.</summary>
    Development,
}

public sealed class SecurityOptions
{
    public SecurityMode Mode { get; set; } = SecurityMode.EasyAuth;

    public string DevelopmentUser { get; set; } = "developer";

    public string[] DevelopmentRoles { get; set; } = Roles.All;
}

/// <summary>
/// Reads the caller from the <c>X-MS-CLIENT-PRINCIPAL</c> header. App Service Authentication validates
/// the token, sets this header and strips any copy a client sends — so this is only safe with
/// authentication enabled on the Function App. The infrastructure template enables it.
/// </summary>
public sealed class RequestAuthorizer(IOptions<SecurityOptions> options)
{
    private const string PrincipalHeader = "X-MS-CLIENT-PRINCIPAL";

    public CallerIdentity? GetCaller(HttpRequest request)
    {
        if (options.Value.Mode == SecurityMode.Development)
        {
            return new CallerIdentity(options.Value.DevelopmentUser, options.Value.DevelopmentRoles);
        }

        var header = request.Headers[PrincipalHeader].ToString();
        if (string.IsNullOrEmpty(header))
        {
            return null;
        }

        try
        {
            var principal = JsonSerializer.Deserialize<ClientPrincipal>(Encoding.UTF8.GetString(Convert.FromBase64String(header)));
            if (principal is null)
            {
                return null;
            }

            var claims = principal.Claims ?? [];
            var roles = claims.Where(c => c.Type == "roles" || c.Type == principal.RoleType).Select(c => c.Value).Distinct().ToList();
            var name = claims.FirstOrDefault(c => c.Type == principal.NameType || c.Type is "name" or "preferred_username")?.Value
                ?? claims.FirstOrDefault(c => c.Type is "azp" or "appid")?.Value;
            return new CallerIdentity(name, roles);
        }
        catch (Exception exception) when (exception is FormatException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Returns <c>null</c> when the caller has one of the roles, otherwise the response to send.</summary>
    public IActionResult? Require(HttpRequest request, params string[] anyOfRoles)
    {
        var caller = GetCaller(request);
        if (caller is null)
        {
            return new UnauthorizedResult();
        }

        // ForbidResult would need ASP.NET Core authentication handlers; a plain status code does not.
        return caller.Roles.Intersect(anyOfRoles, StringComparer.OrdinalIgnoreCase).Any() ? null : new StatusCodeResult(StatusCodes.Status403Forbidden);
    }

    private sealed record ClientPrincipal(
        [property: JsonPropertyName("claims")] List<PrincipalClaim>? Claims,
        [property: JsonPropertyName("name_typ")] string? NameType,
        [property: JsonPropertyName("role_typ")] string? RoleType);

    private sealed record PrincipalClaim(
        [property: JsonPropertyName("typ")] string Type,
        [property: JsonPropertyName("val")] string Value);
}
