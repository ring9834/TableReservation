using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Reservation.Infrastructure;
using Reservation.Tests.Integration.Support;

namespace Reservation.Tests.Integration;

/// <summary>
/// Runs the real API in memory (Development environment) against the test container's database.
/// Migrate-and-seed at start-up is off unless a test asks for it; the outbox workers don't run.
/// The business clock is a <see cref="TestClock"/> at the demo time (18:25 on the seeded evening); the
/// infrastructure clock stays the system clock unless <see cref="InfrastructureClock"/> is set. Demo sign-in is on, with
/// the test passcodes below. Payments use the Fake provider (tests may replace it), and Stripe webhooks are verified with
/// <see cref="WebhookSecret"/> (M8a).
/// </summary>
public sealed class ApiFactory(string connectionString, bool migrateOnStartup = false) : WebApplicationFactory<Program>
{
    public const string HostPasscode = "test-host-passcode";
    public const string PresenterPasscode = "test-presenter-passcode";

    /// <summary>A test-only Stripe webhook signing secret; tests sign their webhooks with it.</summary>
    public const string WebhookSecret = "whsec_test_only_not_a_secret";

    public TestClock Clock { get; } = new(DemoTime.Now);

    public LogCapture Logs { get; } = new();

    /// <summary>Replaces the infrastructure clock (always the system clock otherwise), e.g. to expire payment holds.</summary>
    public TestClock? InfrastructureClock { get; init; }

    /// <summary>Extra configuration, e.g. lower rate limits.</summary>
    public IReadOnlyDictionary<string, string?> Settings { get; init; } = new Dictionary<string, string?>();

    /// <summary>Extra or replaced services, e.g. test-only endpoints.</summary>
    public Action<IServiceCollection>? ExtraServices { get; init; }

    /// <summary>A client on <c>https://localhost</c>, so the Secure session cookie is kept and sent.</summary>
    public HttpClient CreateApiClient() =>
        CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:Reservations", connectionString);
        builder.UseSetting("Database:MigrateOnStartup", migrateOnStartup ? "true" : "false");
        builder.UseSetting("Outbox:RunWorkers", "false");
        builder.UseSetting("Demo:Enabled", "true");
        builder.UseSetting("Demo:HostPasscode", HostPasscode);
        builder.UseSetting("Demo:PresenterPasscode", PresenterPasscode);
        builder.UseSetting("Payments:Provider", "Fake");
        builder.UseSetting("Stripe:WebhookSecret", WebhookSecret);
        foreach (var (key, value) in Settings)
        {
            builder.UseSetting(key, value);
        }

        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<TimeProvider>(Clock);
            if (InfrastructureClock is not null)
            {
                services.AddKeyedSingleton<TimeProvider>(Clocks.Infrastructure, InfrastructureClock);
            }

            services.AddSingleton<ILoggerProvider>(Logs);
            ExtraServices?.Invoke(services);
        });
    }
}
