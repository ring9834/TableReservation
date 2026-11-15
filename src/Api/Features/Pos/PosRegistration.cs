using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.Options;
using Reservation.Api.Auth;
using Reservation.Api.Features.Guests;
using Reservation.Api.Realtime;
using Reservation.Contracts.Pos;
using Reservation.Domain.Bookings;
using Reservation.Infrastructure.Outbox;
using Reservation.Infrastructure.Pos;

namespace Reservation.Api.Features.Pos;

internal static class PosRegistration
{
    /// <summary>
    /// The POS consumers and the inbound path (Design §6, §6.7): the projection handler, the guest-visit consumer, the
    /// webhook handler and processor, and the <c>posLogChanged</c> push. With <c>Demo:Enabled</c> the mock signs its
    /// simulated bills, so <c>Pos:WebhookSecret</c> is required at start-up.
    /// </summary>
    public static IServiceCollection AddPos(this IServiceCollection services)
    {
        services.AddOptions<PosOptions>()
            .Validate<IOptions<DemoOptions>>(
                (o, demo) => !demo.Value.Enabled || !string.IsNullOrWhiteSpace(o.WebhookSecret),
                "Demo:Enabled is true, so Pos:WebhookSecret must be set (any value locally: it signs the simulated POS bills; dotnet user-secrets set Pos:WebhookSecret \"<any value>\" --project src/Api).")
            .ValidateOnStart();

        // Replaces Infrastructure's no-op: POS log changes reach the screens after commit (Design §6.7).
        services.AddSingleton<IPosLogNotifier, PosLogPusher>();

        services.AddScoped<CloseBillHandler>();
        services.AddScoped<PosWebhookHandler>();
        services.AddScoped<PosWebhookProcessor>();
        services.AddEventHandler<BookingCreated, PosProjectionHandler>();
        services.AddEventHandler<BookingSeated, PosProjectionHandler>();
        services.AddEventHandler<BookingCompleted, GuestVisitHandler>();
        services.AddEventHandler<BookingBillAttached, GuestVisitHandler>();
        return services;
    }

    /// <summary>The POS message records as OpenAPI components, so the partner contract and orval see them (Design §6.7).</summary>
    public static OpenApiOptions AddPosMessageSchemas(this OpenApiOptions options)
    {
        Type[] messages = [typeof(PosUpsertMember), typeof(PosOpenTable), typeof(PosApplyDeposit), typeof(PosBillClosed)];
        return options.AddDocumentTransformer(async (document, context, cancellationToken) =>
        {
            foreach (var message in messages)
            {
                var schema = await context.GetOrCreateSchemaAsync(message, null, cancellationToken);
                document.AddComponent(message.Name, schema);
            }
        });
    }
}
