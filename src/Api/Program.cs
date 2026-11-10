using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Reservation.Api;
using Reservation.Api.Health;
using Reservation.Infrastructure;
using Reservation.Infrastructure.Demo;

var builder = WebApplication.CreateBuilder(args);

// `dotnet ef` builds the host without secrets (e.g. in CI); it never connects for model-only commands.
var connectionString = builder.Configuration.GetConnectionString("Reservations")
    ?? (EF.IsDesignTime
        ? "Host=localhost;Database=reservations"
        : throw new InvalidOperationException(
            "ConnectionStrings:Reservations is not set. Locally, set it with dotnet user-secrets (see CLAUDE.md)."));

// Failed saves are logged once, by the central exception handler (ADR-004), not again by EF Core.
builder.Services.AddInfrastructure(connectionString, options => options.ConfigureWarnings(w => w.Log(
    (CoreEventId.SaveChangesFailed, LogLevel.Debug),
    (CoreEventId.OptimisticConcurrencyException, LogLevel.Debug))));
builder.AddApi();
builder.Services.AddSingleton<OutboxHealthCheck>();
builder.Services.AddHealthChecks()
    .AddCheck<DatabaseHealthCheck>("database", tags: [HealthTags.Ready])
    .AddCheck<OutboxHealthCheck>("outbox", tags: [HealthTags.Ready]);

var app = builder.Build();

// D8: in Development only, migrate and seed at start-up. CI and AWS use the migration bundle (ADR-005).
if (app.Environment.IsDevelopment() && app.Configuration.GetValue<bool>("Database:MigrateOnStartup"))
{
    await using var scope = app.Services.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<DatabaseInitializer>().InitializeAsync(app.Lifetime.ApplicationStopping);
}

app.UseApi();

// Live: the process is up. Ready: its dependencies answer (database, and outbox lag: Degraded still returns 200).
// Public and never rate limited, so load balancer probes always get an answer.
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false })
    .AllowAnonymous().DisableRateLimiting();
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains(HealthTags.Ready) })
    .AllowAnonymous().DisableRateLimiting();
app.MapApi();

await app.RunAsync();

public partial class Program;
