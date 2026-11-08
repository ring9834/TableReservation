using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Reservation.Tests.Integration;

/// <summary>Runs the real API in memory against the test container's database.</summary>
public sealed class ApiFactory(string connectionString) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder) =>
        builder.UseSetting("ConnectionStrings:Reservations", connectionString);
}
