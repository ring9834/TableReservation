using Microsoft.AspNetCore.OpenApi;
using Reservation.Api.Auth;
using Reservation.Domain.Bookings;
using Reservation.Infrastructure.Outbox;

namespace Reservation.Api.Realtime;

internal static class RealtimeRegistration
{
    /// <summary>SignalR, the broadcaster and a <see cref="RealtimeNotifier{T}"/> for every event in the catalogue.</summary>
    public static IServiceCollection AddRealtime(this IServiceCollection services)
    {
        services.AddSignalR();
        services.AddSingleton<IVenueBroadcaster, HubVenueBroadcaster>();
        foreach (var eventType in new EventTypeRegistry().All.Select(e => e.Type))
        {
            services.AddEventHandler(eventType, typeof(RealtimeNotifier<>).MakeGenericType(eventType), idempotent: false);
        }

        // Reads only, so unwrapped like the notifier (Design §4.3 "Waitlist suggester", §15.1).
        services.AddEventHandler<TableCleared, WaitlistSuggester>(idempotent: false);
        services.AddEventHandler<BookingCancelled, WaitlistSuggester>(idempotent: false);

        services.AddSingleton<WaitlistOverdueJob>();
        services.AddHostedService(provider => provider.GetRequiredService<WaitlistOverdueJob>());

        return services;
    }

    /// <summary>
    /// <c>/hubs/venue</c> for the <c>Host</c> policy (a <c>Presenter</c> passes too). Cookie only: no
    /// <c>access_token</c> query string in the demo (ADR-004 covers the pilot). The connection closes when the session
    /// expires.
    /// </summary>
    public static IEndpointRouteBuilder MapRealtime(this IEndpointRouteBuilder app)
    {
        app.MapHub<VenueHub>(VenueHub.Path, options => options.CloseOnAuthenticationExpiration = true)
            .RequireAuthorization(Policies.Host);
        return app;
    }

    /// <summary>Adds the notice records to the OpenAPI components, so orval generates them (ADR-004, Design §15).</summary>
    public static OpenApiOptions AddNoticeSchemas(this OpenApiOptions options)
    {
        Type[] notices = [typeof(FloorPlanChangedNotice), typeof(WaitlistChangedNotice), typeof(MessageLogChangedNotice), typeof(PosLogChangedNotice)];
        return options.AddDocumentTransformer(async (document, context, cancellationToken) =>
        {
            foreach (var notice in notices)
            {
                var schema = await context.GetOrCreateSchemaAsync(notice, null, cancellationToken);
                document.AddComponent(notice.Name, schema);
            }
        });
    }
}
