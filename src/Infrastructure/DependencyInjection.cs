using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Npgsql;
using Reservation.Infrastructure.Audit;
using Reservation.Infrastructure.Demo;
using Reservation.Infrastructure.Idempotency;
using Reservation.Infrastructure.Messaging;
using Reservation.Infrastructure.Outbox;
using Reservation.Infrastructure.Persistence;

namespace Reservation.Infrastructure;

public static class DependencyInjection
{
    /// <summary>
    /// The two clocks, the database (one <see cref="NpgsqlDataSource"/>, shared with the health checks), the EF Core
    /// context with the outbox and message interceptors, the outbox workers and the audit consumer, message delivery,
    /// the request venue context, idempotency keys and the demo seed and reset. Needs <c>IConfiguration</c> for the
    /// <c>Outbox</c> and <c>Messaging</c> options.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="connectionString">PostgreSQL connection string; ignored when a data source is already registered.</param>
    /// <param name="configureDatabase">Extra EF Core options (tests use it to relax warnings).</param>
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services, string connectionString, Action<DbContextOptionsBuilder>? configureDatabase = null)
    {
        // Business clock (the system clock, or DemoClock when Demo:Clock:Mode is Fixed, Design §17) and infrastructure
        // clock (always the system clock), Design §5.6. Tests register their own business clock after this.
        services.AddOptions<DemoClockOptions>()
            .BindConfiguration(DemoClockOptions.Section)
            .Validate(o => o.IsValid, "Demo:Clock:Mode is Fixed, so Demo:Clock:FixedLocalTime must be set (e.g. 18:25).")
            .ValidateOnStart();
        services.AddOptions<DemoDataOptions>().BindConfiguration(DemoDataOptions.Section);
        services.TryAddSingleton(provider =>
            DemoClock.Create(provider.GetRequiredService<IOptions<DemoClockOptions>>().Value, TimeProvider.System));
        services.TryAddKeyedSingleton(Clocks.Infrastructure, TimeProvider.System);

        services.TryAddSingleton(_ => NpgsqlDataSource.Create(connectionString));
        services.AddDbContext<ReservationsDbContext>((provider, options) =>
        {
            options.UseReservationsDatabase(provider.GetRequiredService<NpgsqlDataSource>())
                .AddInterceptors(
                    provider.GetRequiredService<OutboxSaveChangesInterceptor>(),
                    provider.GetRequiredService<MessageChangeInterceptor>());
            configureDatabase?.Invoke(options);
        });

        // Set per request from the route's venueId after the venue check (Design §14.1); empty elsewhere.
        services.TryAddScoped<VenueContext>();
        services.TryAddScoped<IVenueContext>(provider => provider.GetRequiredService<VenueContext>());

        services.AddScoped<IdempotencyStore>();
        services.AddSingleton<IdempotencyHousekeeping>();
        services.AddHostedService(provider => provider.GetRequiredService<IdempotencyHousekeeping>());

        AddOutbox(services);
        AddMessaging(services);

        services.AddScoped<DemoSeeder>();
        services.AddScoped<DemoReset>();
        services.AddScoped<DatabaseInitializer>();
        return services;
    }

    /// <summary>
    /// Message delivery (Design §7.1, §7.2): the worker, its signal, the demo phone-panel sender and a notifier that pushes
    /// nothing. The API replaces the notifier with its SignalR push; tests may replace the sender.
    /// </summary>
    private static void AddMessaging(IServiceCollection services)
    {
        services.AddOptions<MessagingOptions>()
            .BindConfiguration(MessagingOptions.Section)
            .Validate(o => o.IsValid, "Messaging:PollInterval and Messaging:BatchSize must be positive; Messaging:PhonePanelDeliveryDelay not negative.")
            .ValidateOnStart();

        services.AddSingleton<MessageSignal>();
        services.AddScoped<MessageChangeInterceptor>();
        services.TryAddSingleton<IMessageChangeNotifier, NullMessageChangeNotifier>();
        services.TryAddScoped<IMessageSender, PhonePanelSender>();
        services.AddSingleton<MessageDeliveryWorker>();
        services.AddHostedService(provider => provider.GetRequiredService<MessageDeliveryWorker>());
    }

    /// <summary>The provider settings every <see cref="ReservationsDbContext"/> uses (also in tests).</summary>
    public static TBuilder UseReservationsDatabase<TBuilder>(this TBuilder options, NpgsqlDataSource dataSource)
        where TBuilder : DbContextOptionsBuilder
    {
        ArgumentNullException.ThrowIfNull(options);
        options.UseNpgsql(dataSource).UseSnakeCaseNamingConvention();
        return options;
    }

    private static void AddOutbox(IServiceCollection services)
    {
        services.AddOptions<OutboxOptions>()
            .BindConfiguration(OutboxOptions.Section)
            .Validate(o => o.PollInterval > TimeSpan.Zero && o.BatchSize > 0, "Outbox:PollInterval and Outbox:BatchSize must be positive.")
            .ValidateOnStart();

        var registry = new EventTypeRegistry();
        services.AddSingleton(registry);
        services.AddSingleton<EventEnvelopeSerializer>();
        services.AddSingleton<OutboxSignal>();
        services.AddScoped<RequestContext>();
        services.AddScoped<IRequestContext>(provider => provider.GetRequiredService<RequestContext>());
        services.AddScoped<OutboxSaveChangesInterceptor>();

        services.AddSingleton<OutboxDispatcher>();
        services.AddHostedService(provider => provider.GetRequiredService<OutboxDispatcher>());
        services.AddSingleton<OutboxHousekeeping>();
        services.AddHostedService(provider => provider.GetRequiredService<OutboxHousekeeping>());

        foreach (var eventType in registry.All.Select(e => e.Type))
        {
            services.AddEventHandler(eventType, typeof(AuditHandler<>).MakeGenericType(eventType));
        }
    }
}
