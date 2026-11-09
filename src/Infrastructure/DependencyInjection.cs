using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Reservation.Infrastructure.Demo;
using Reservation.Infrastructure.Persistence;

namespace Reservation.Infrastructure;

public static class DependencyInjection
{
    /// <summary>
    /// The database (one <see cref="NpgsqlDataSource"/>, shared with the health check), the EF Core context,
    /// the demo venue context and the demo seed and reset.
    /// </summary>
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, string connectionString)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(_ => NpgsqlDataSource.Create(connectionString));
        services.AddDbContext<ReservationsDbContext>((provider, options) => options.UseReservationsDatabase(provider.GetRequiredService<NpgsqlDataSource>()));

        // The demo session is always Golden Dragon (Design §17); M4 reads it from the session instead.
        services.AddSingleton<IVenueContext>(new FixedVenueContext(GoldenDragon.VenueId));

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
}
