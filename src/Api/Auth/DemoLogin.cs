using System.Security.Cryptography;
using System.Text;
using FluentValidation;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Reservation.Api.Common;
using Reservation.Api.Pipeline;
using Reservation.Infrastructure;
using Reservation.Infrastructure.Demo;

namespace Reservation.Api.Auth;

/// <param name="Passcode">The host or presenter passcode.</param>
internal sealed record DemoLoginRequest(string Passcode);

/// <param name="Role">The role of the session just issued; the front end opens <c>/host</c> or <c>/presenter</c>.</param>
internal sealed record DemoLoginResponse(StaffRole Role);

internal enum StaffRole
{
    Host,
    Presenter,
}

internal sealed class DemoLoginRequestValidator : AbstractValidator<DemoLoginRequest>
{
    public const int MaxLength = 200;

    public DemoLoginRequestValidator()
    {
        RuleFor(x => x.Passcode).NotEmpty().WithErrorCode("passcode_required")
            .MaximumLength(MaxLength).WithErrorCode("passcode_too_long");
    }
}

/// <summary>
/// <c>POST /demo-login</c> (Design §14, §17): compares the passcode in constant time with both configured passcodes
/// and issues the demo session cookie. A wrong passcode is 401 <c>not_signed_in</c>, logged as a warning without the
/// passcode. Limited to 5 attempts a minute per IP. Mapped only when <c>Demo:Enabled</c> is true.
/// </summary>
internal sealed partial class DemoLoginEndpoint : IEndpoint
{
    public void Map(IEndpointRouteBuilder api)
    {
        if (!api.ServiceProvider.GetRequiredService<IOptions<DemoOptions>>().Value.Enabled)
        {
            return;
        }

        api.MapPost("/demo-login", HandleAsync)
            .WithName("demoLogin")
            .WithTags("Demo")
            .AllowAnonymous()
            .RequireRateLimiting(RateLimits.DemoLogin)
            .WithValidation<DemoLoginRequest>()
            .Produces<DemoLoginResponse>()
            .ProducesApiProblem(StatusCodes.Status401Unauthorized, StatusCodes.Status429TooManyRequests);
    }

    private static async Task<IResult> HandleAsync(
        DemoLoginRequest request,
        HttpContext http,
        IOptions<DemoOptions> options,
        [FromKeyedServices(Clocks.Infrastructure)] TimeProvider clock,
        ILogger<DemoLoginEndpoint> logger)
    {
        var role = Match(request.Passcode, options.Value);
        if (role is null)
        {
            LogFailed(logger);
            return ApiProblems.Result(StatusCodes.Status401Unauthorized, ProblemCodes.NotSignedIn);
        }

        var roleName = role == StaffRole.Presenter ? Roles.Presenter : Roles.Host;
        await http.SignInAsync(
            DemoSession.Scheme,
            DemoSession.Principal(roleName, GoldenDragon.VenueId),
            new AuthenticationProperties
            {
                IsPersistent = true,
                AllowRefresh = false,
                ExpiresUtc = clock.GetUtcNow() + DemoSession.Lifetime,
            });
        LogSignedIn(logger, roleName);
        return TypedResults.Ok(new DemoLoginResponse(role.Value));
    }

    /// <summary>Both passcodes are always compared, as fixed-length hashes, so timing reveals nothing.</summary>
    private static StaffRole? Match(string passcode, DemoOptions options)
    {
        var given = Hash(passcode);
        var isHost = CryptographicOperations.FixedTimeEquals(given, Hash(options.HostPasscode ?? string.Empty));
        var isPresenter = CryptographicOperations.FixedTimeEquals(given, Hash(options.PresenterPasscode ?? string.Empty));
        return isPresenter ? StaffRole.Presenter : isHost ? StaffRole.Host : null;
    }

    private static byte[] Hash(string value) => SHA256.HashData(Encoding.UTF8.GetBytes(value));

    [LoggerMessage(Level = LogLevel.Warning, Message = "Demo sign-in failed: wrong passcode")]
    private static partial void LogFailed(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Demo sign-in as {Role}")]
    private static partial void LogSignedIn(ILogger logger, string role);
}
