using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Reservation.Api.Common;
using Reservation.Domain.Common;
using Reservation.Infrastructure;

namespace Reservation.Api.Auth;

/// <summary>Staff roles. <see cref="Presenter"/> is demo-only: host permissions plus the demo controls and logs (Design §17).</summary>
internal static class Roles
{
    public const string Host = "Host";
    public const string Presenter = "Presenter";
}

/// <summary>Authorization policies; every endpoint names one or allows anonymous callers (deny by default).</summary>
internal static class Policies
{
    /// <summary>Role <c>Host</c> or <c>Presenter</c>, for the venue in the route.</summary>
    public const string Host = "Host";

    /// <summary>Role <c>Presenter</c>, for the venue in the route.</summary>
    public const string Presenter = "Presenter";
}

/// <summary>Section <c>Demo</c>. Passcodes come from user secrets locally and Secrets Manager in AWS.</summary>
internal sealed class DemoOptions
{
    public const string Section = "Demo";

    /// <summary>Maps <c>/demo-login</c> (and later <c>/demo/*</c>). Never true in the pilot.</summary>
    public bool Enabled { get; set; }

    public string? HostPasscode { get; set; }

    public string? PresenterPasscode { get; set; }
}

/// <summary>
/// The demo session (Design §17, ADR-004): a cookie issued by <c>/demo-login</c> for a passcode, with a fixed staff
/// identity for Golden Dragon and role <c>Host</c> or <c>Presenter</c>. <c>HttpOnly</c>, <c>Secure</c>,
/// <c>SameSite=Strict</c>, 12 hours, not sliding. Timed on the infrastructure clock, so the demo clock never
/// stretches or ends a session. Cognito replaces this provider in the pilot; the policies stay.
/// </summary>
internal static class DemoSession
{
    public const string Scheme = "DemoSession";

    /// <summary><c>__Host-</c>: Secure, path <c>/</c>, no domain, so only the API's own host gets it.</summary>
    public const string CookieName = "__Host-demo-session";

    public const string SubjectClaim = "sub";
    public const string RoleClaim = "role";
    public const string VenueClaim = "venue_ids";

    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(12);

    public static IServiceCollection AddDemoSession(this IServiceCollection services)
    {
        services.AddOptions<DemoOptions>()
            .BindConfiguration(DemoOptions.Section)
            .Validate(
                o => !o.Enabled || (!string.IsNullOrWhiteSpace(o.HostPasscode) && !string.IsNullOrWhiteSpace(o.PresenterPasscode)
                    && !string.Equals(o.HostPasscode, o.PresenterPasscode, StringComparison.Ordinal)),
                "Demo:Enabled is true, so Demo:HostPasscode and Demo:PresenterPasscode must be set (user secrets locally) and differ.")
            .ValidateOnStart();

        services.AddAuthentication(Scheme).AddCookie(Scheme, options =>
        {
            options.Cookie.Name = CookieName;
            options.Cookie.HttpOnly = true;
            options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.Cookie.Path = "/";
            options.ExpireTimeSpan = Lifetime;
            options.SlidingExpiration = false;
            options.Events.OnRedirectToLogin = context =>
                WriteProblemAsync(context.HttpContext, StatusCodes.Status401Unauthorized, ProblemCodes.NotSignedIn);
            options.Events.OnRedirectToAccessDenied = context =>
                WriteProblemAsync(context.HttpContext, StatusCodes.Status403Forbidden, ErrorCodes.Forbidden);
        });
        services.AddOptions<CookieAuthenticationOptions>(Scheme).PostConfigure<IServiceProvider>((options, provider) =>
            options.TimeProvider = provider.GetRequiredKeyedService<TimeProvider>(Clocks.Infrastructure));

        services.AddSingleton<IAuthorizationHandler, VenueAccessHandler>();
        services.AddAuthorizationBuilder()
            .SetFallbackPolicy(new AuthorizationPolicyBuilder(Scheme).RequireAuthenticatedUser().Build())
            .AddPolicy(Policies.Host, policy => policy
                .AddAuthenticationSchemes(Scheme)
                .RequireRole(Roles.Host, Roles.Presenter)
                .AddRequirements(new VenueAccessRequirement()))
            .AddPolicy(Policies.Presenter, policy => policy
                .AddAuthenticationSchemes(Scheme)
                .RequireRole(Roles.Presenter)
                .AddRequirements(new VenueAccessRequirement()));

        services.AddHttpContextAccessor();
        services.AddScoped<IUserContext, HttpUserContext>();
        return services;
    }

    /// <summary>The fixed staff identity for a role: ID <c>demo-host</c> or <c>demo-presenter</c>, venue Golden Dragon.</summary>
    public static ClaimsPrincipal Principal(string role, VenueId venueId)
    {
        var subject = role == Roles.Presenter ? "demo-presenter" : "demo-host";
        Claim[] claims = [new(SubjectClaim, subject), new(RoleClaim, role), new(VenueClaim, venueId.Value.ToString())];
        return new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme, SubjectClaim, RoleClaim));
    }

    private static async Task WriteProblemAsync(HttpContext context, int status, string code)
    {
        context.Response.StatusCode = status;
        await context.RequestServices.GetRequiredService<IProblemDetailsService>().WriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            ProblemDetails = ApiProblems.Create(status, code),
        });
    }
}

/// <summary>ADR-004 rule 1: the caller works at the venue in the route.</summary>
internal sealed class VenueAccessRequirement : IAuthorizationRequirement;

/// <summary>Compares the route's <c>venueId</c> with the <c>venue_ids</c> claims; routes without a venue pass.</summary>
internal sealed class VenueAccessHandler : AuthorizationHandler<VenueAccessRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, VenueAccessRequirement requirement)
    {
        if (context.Resource is not HttpContext http || http.GetRouteValue("venueId") is not string routeVenue)
        {
            context.Succeed(requirement);
            return Task.CompletedTask;
        }

        if (Guid.TryParse(routeVenue, out var venueId)
            && context.User.FindAll(DemoSession.VenueClaim).Any(c => Guid.TryParse(c.Value, out var id) && id == venueId))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}

/// <summary>Who is calling (Design §17): a staff actor from the session, or an anonymous guest.</summary>
internal interface IUserContext
{
    bool IsStaff { get; }

    Actor Actor { get; }

    IReadOnlyList<VenueId> VenueIds { get; }
}

internal sealed class HttpUserContext(IHttpContextAccessor accessor) : IUserContext
{
    private ClaimsPrincipal? User => accessor.HttpContext?.User;

    public bool IsStaff => User?.Identity?.IsAuthenticated == true && User.FindFirst(DemoSession.RoleClaim) is not null;

    public Actor Actor => IsStaff
        ? Actor.ForStaff(User!.FindFirst(DemoSession.SubjectClaim)!.Value, User.FindFirst(DemoSession.RoleClaim)!.Value)
        : Actor.ForGuest(null);

    public IReadOnlyList<VenueId> VenueIds =>
        User?.FindAll(DemoSession.VenueClaim).Select(c => Guid.TryParse(c.Value, out var id) ? new VenueId(id) : (VenueId?)null)
            .OfType<VenueId>().ToList() ?? [];
}
