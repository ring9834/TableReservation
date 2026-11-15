using Reservation.Api.Features.GuestBookings;

namespace Reservation.Api.GuestLinks;

internal static class GuestLinkRegistration
{
    /// <summary>
    /// The signer, the link builder and their options (Design §7.3). The API refuses to start without a signing key and
    /// a base URL, because every confirmation carries a manage-booking link.
    /// </summary>
    public static IServiceCollection AddGuestLinks(this IServiceCollection services)
    {
        services.AddOptions<GuestLinkOptions>()
            .BindConfiguration(GuestLinkOptions.Section)
            .Validate(
                o => o.HasValidKeys,
                "GuestLinks:SigningKey must be set to at least 32 characters (dotnet user-secrets set GuestLinks:SigningKey \"<random value>\" --project src/Api locally, Secrets Manager in AWS); every GuestLinks:PreviousKeys value must be too, under a key ID other than GuestLinks:KeyId.")
            .Validate(o => o.HasValidKeyId, "GuestLinks:KeyId must be 1-16 ASCII letters or digits.")
            .Validate(
                o => o.HasValidBaseUrl,
                "GuestLinks:BaseUrl must be the guest web app's absolute http(s) origin, e.g. https://localhost:3000.")
            .ValidateOnStart();
        services.AddSingleton<GuestLinkSigner>();
        services.AddSingleton<GuestLinkUrls>();
        services.AddScoped<GuestBookingHandler>();
        return services;
    }
}
