using System.Text.Encodings.Web;
using AssetTracker.Core.Security;
using AssetTracker.Infrastructure.Demo;
using AssetTracker.Web.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace AssetTracker.Web.Demo;

/// <summary>
/// Demo mode replacement for Windows Authentication: every request is signed in as the identity chosen on
/// <c>/Demo</c> (cookie), else the default demo user. Emits the same claim shapes as Negotiate (see
/// <see cref="DemoIdentity.ToPrincipal"/>).
/// </summary>
public sealed class DemoAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, DemoIdentityCookie cookie)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Demo";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var principal = cookie.Read(Context).ToPrincipal(SchemeName);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName)));
    }
}

/// <summary>
/// Demo mode <see cref="ICurrentUser"/>: the seeding/reset actor when one is active for this async flow
/// (<see cref="AmbientCurrentUser"/>), else the signed-in HTTP user, else the background-job identity
/// (<c>system:worker</c>, no roles) because the jobs run in the web process.
/// </summary>
public sealed class DemoCurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    private readonly HttpCurrentUser _http = new(accessor);

    private ICurrentUser Effective =>
        AmbientCurrentUser.Current
        ?? (accessor.HttpContext?.User.Identity?.IsAuthenticated == true ? _http : DemoSystemUser.Instance);

    public string Name => Effective.Name;
    public string? Sid => Effective.Sid;
    public AppRoles Roles => Effective.Roles;
}
