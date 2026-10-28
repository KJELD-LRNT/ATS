using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AssetTracker.Tests.Infrastructure;

/// <summary>
/// Simulates Windows Authentication: builds a principal with Name and PrimarySid claims from request headers, so the
/// real claims transformation (roles from <c>RoleAssignments</c>) and policies run unchanged.
/// </summary>
public class TestAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Test";
    public const string UserHeader = "X-Test-User";
    public const string SidHeader = "X-Test-Sid";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(UserHeader, out var name) || string.IsNullOrEmpty(name))
            return Task.FromResult(AuthenticateResult.NoResult());

        var principal = BuildPrincipal(name!, Request.Headers[SidHeader].ToString());
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName)));
    }

    public static ClaimsPrincipal BuildPrincipal(string name, string sid)
    {
        var claims = new List<Claim> { new(ClaimTypes.Name, name), new(ClaimTypes.PrimarySid, sid) };
        return new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName, ClaimTypes.Name, ClaimTypes.Role));
    }
}
