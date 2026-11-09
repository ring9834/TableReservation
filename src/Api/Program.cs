using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Npgsql;
using Reservation.Api.Health;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Reservations")
    ?? throw new InvalidOperationException(
        "ConnectionStrings:Reservations is not set. Locally, set it with dotnet user-secrets (see CLAUDE.md).");

builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(connectionString));
builder.Services.AddHealthChecks()
    .AddCheck<DatabaseHealthCheck>("database", tags: [HealthTags.Ready]);

var app = builder.Build();

// Live: the process is up. Ready: its dependencies answer (database now; outbox lag in M3).
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains(HealthTags.Ready) });

app.Run();

public partial class Program;
