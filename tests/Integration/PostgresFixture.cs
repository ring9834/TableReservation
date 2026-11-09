using Npgsql;
using Testcontainers.PostgreSql;

namespace Reservation.Tests.Integration;

/// <summary>One real PostgreSQL container shared by every test in the <see cref="PostgresCollection"/>.</summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:16-alpine").Build();

    public string ConnectionString => _container.GetConnectionString();

    /// <summary>A new, empty database in the shared container, so tests never see each other's data.</summary>
    public async Task<string> CreateDatabaseAsync()
    {
        var name = $"test_{Guid.NewGuid():N}";
        await using (var connection = new NpgsqlConnection(ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", connection);
            await command.ExecuteNonQueryAsync();
        }

        return new NpgsqlConnectionStringBuilder(ConnectionString) { Database = name }.ConnectionString;
    }

    public async ValueTask InitializeAsync() => await _container.StartAsync();

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();
}

[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}
