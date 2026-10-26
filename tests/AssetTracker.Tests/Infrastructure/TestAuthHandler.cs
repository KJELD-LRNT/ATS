using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AssetTracker.Tests.Infrastructure;

/// <summary>
/// Simulates Windows Authentication: builds a principal with Name, PrimarySid and GroupSid claims
/// from request headers, so the real claims transformation and policies run unchanged.
/// </summary>
public class TestAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Test";
    public const string UserHeader = "X-Test-User";
    public const string SidHeader = "X-Test-Sid";
    public const string GroupsHeader = "X-Test-Groups";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(UserHeader, out var name) || string.IsNullOrEmpty(name))
            return Task.FromResult(AuthenticateResult.NoResult());

        var principal = BuildPrincipal(name!, Request.Headers[SidHeader].ToString(),
            Request.Headers[GroupsHeader].ToString().Split(',', StringSplitOptions.RemoveEmptyEntries));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName)));
    }

    public static ClaimsPrincipal BuildPrincipal(string name, string sid, IEnumerable<string> groupSids)
    {
        var claims = new List<Claim> { new(ClaimTypes.Name, name), new(ClaimTypes.PrimarySid, sid) };
        claims.AddRange(groupSids.Select(g => new Claim(ClaimTypes.GroupSid, g)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName, ClaimTypes.Name, ClaimTypes.Role));
    }
}
