using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
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

builder.Services.AddInfrastructure(connectionString);
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

// Live: the process is up. Ready: its dependencies answer (database, and outbox lag: Degraded still returns 200).
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains(HealthTags.Ready) });

await app.RunAsync();

public partial class Program;
