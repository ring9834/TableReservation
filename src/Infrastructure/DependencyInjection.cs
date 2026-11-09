using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Reservation.Infrastructure.Audit;
using Reservation.Infrastructure.Demo;
using Reservation.Infrastructure.Outbox;
using Reservation.Infrastructure.Persistence;

namespace Reservation.Infrastructure;

public static class DependencyInjection
{
    /// <summary>
    /// The two clocks, the database (one <see cref="NpgsqlDataSource"/>, shared with the health checks), the EF Core
    /// context with the outbox interceptor, the outbox workers and the audit consumer, the demo venue context and the
    /// demo seed and reset. Needs <c>IConfiguration</c> for the <c>Outbox</c> options.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="connectionString">PostgreSQL connection string; ignored when a data source is already registered.</param>
    /// <param name="configureDatabase">Extra EF Core options (tests use it to relax warnings).</param>
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services, string connectionString, Action<DbContextOptionsBuilder>? configureDatabase = null)
    {
        // Business clock (DemoClock replaces it in the demo) and infrastructure clock (always the system clock), Design §5.6.
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddKeyedSingleton(Clocks.Infrastructure, TimeProvider.System);

        services.TryAddSingleton(_ => NpgsqlDataSource.Create(connectionString));
        services.AddDbContext<ReservationsDbContext>((provider, options) =>
        {
            options.UseReservationsDatabase(provider.GetRequiredService<NpgsqlDataSource>())
                .AddInterceptors(provider.GetRequiredService<OutboxSaveChangesInterceptor>());
            configureDatabase?.Invoke(options);
        });

        // The demo session is always Golden Dragon (Design §17); M4 reads it from the session instead.
        services.AddSingleton<IVenueContext>(new FixedVenueContext(GoldenDragon.VenueId));

        AddOutbox(services);

        services.AddScoped<DemoSeeder>();
        services.AddScoped<DemoReset>();
        services.AddScoped<DatabaseInitializer>();
        return services;
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
