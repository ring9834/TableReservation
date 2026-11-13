using Reservation.Domain.Bookings;
using Reservation.Domain.Waitlist;
using Reservation.Infrastructure.Outbox;

namespace Reservation.Api.Messaging;

internal static class MessagingRegistration
{
    /// <summary>Templates, the composer and the "Notifications" consumer (Design §4.3, §7), wrapped in the inbox decorator.</summary>
    public static IServiceCollection AddMessages(this IServiceCollection services)
    {
        services.AddSingleton<ITemplateStore, ResxTemplateStore>();
        services.AddSingleton<MessageComposer>();
        services.AddEventHandler<BookingCreated, NotificationHandler>();
        services.AddEventHandler<WaitlistNotified, NotificationHandler>();
        return services;
    }
}
