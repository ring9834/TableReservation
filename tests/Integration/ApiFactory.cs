using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Reservation.Tests.Integration;

/// <summary>
/// Runs the real API in memory (Development environment) against the test container's database.
/// Migrate-and-seed at start-up is off unless a test asks for it.
/// </summary>
public sealed class ApiFactory(string connectionString, bool migrateOnStartup = false) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:Reservations", connectionString);
        builder.UseSetting("Database:MigrateOnStartup", migrateOnStartup ? "true" : "false");
    }
}
