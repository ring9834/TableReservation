using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Reservation.Tests.Integration.Api;

/// <summary>
/// The committed contract <c>docs/api/openapi.json</c> must match what the API serves (ADR-004, D12). After an
/// intended API change, refresh it with <c>UPDATE_OPENAPI=1</c> (see CLAUDE.md) and commit it; CI's oasdiff job then
/// compares it with main for breaking changes.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class OpenApiDocumentTests(PostgresFixture postgres) : ApiTestBase(postgres)
{
    private static readonly JsonSerializerOptions _pretty = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    [Fact]
    public async Task The_committed_document_matches_the_api()
    {
        var served = JsonNode.Parse(await Client.GetStringAsync("/openapi/v1.json", Ct))!;
        var actual = served.ToJsonString(_pretty).ReplaceLineEndings("\n") + "\n";
        var path = Path.Combine(RepositoryRoot(), "docs", "api", "openapi.json");

        if (Environment.GetEnvironmentVariable("UPDATE_OPENAPI") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, actual, Ct);
            return;
        }

        Assert.True(File.Exists(path), $"{path} is missing. Run the tests with UPDATE_OPENAPI=1 to create it.");
        var committed = (await File.ReadAllTextAsync(path, Ct)).ReplaceLineEndings("\n");
        Assert.True(
            committed == actual,
            "docs/api/openapi.json differs from the API. If the change is intended, run the tests with UPDATE_OPENAPI=1 and commit the file.");
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ReservationDemo.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("Could not find the repository root (ReservationDemo.slnx).");
    }
}
