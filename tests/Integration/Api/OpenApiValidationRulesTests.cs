using System.Text.Json.Nodes;
using Reservation.Tests.Integration.Support;

namespace Reservation.Tests.Integration.Api;

/// <summary>
/// The FluentValidation rules that a JSON schema can express reach the OpenAPI document, and from there the generated
/// zod schemas (ADR-004 "How it is used" 4, Design §18.4).
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class OpenApiValidationRulesTests(PostgresFixture postgres) : ApiTestBase(postgres)
{
    [Fact]
    public async Task Create_booking_request_carries_lengths_ranges_and_email_format()
    {
        var properties = await PropertiesOfAsync("CreateBookingRequest");

        Assert.Equal((1, 80), Lengths(properties["name"]!));
        Assert.Equal((null, 500), Lengths(properties["notes"]!));
        Assert.Equal((null, 500), Lengths(properties["allergyNotes"]!));
        Assert.Equal((null, 50), Lengths(properties["occasion"]!));
        Assert.Equal((null, 254), Lengths(properties["email"]!));
        Assert.Equal("email", (string?)properties["email"]!["format"]);
        Assert.Equal(1, (int?)properties["partySize"]!["minimum"]);
        Assert.Equal(20, (int?)properties["partySize"]!["maximum"]);
    }

    [Fact]
    public async Task Custom_rules_and_enums_are_left_alone()
    {
        var properties = await PropertiesOfAsync("CreateBookingRequest");

        // Required, but the NZ mobile check (libphonenumber) cannot be said in JSON schema; it stays a server field error.
        Assert.Equal((1, null), Lengths(properties["mobile"]!));
        Assert.Null(properties["mobile"]!["pattern"]);
        Assert.Equal("#/components/schemas/Language", (string?)properties["language"]!["$ref"]);
    }

    [Fact]
    public async Task Demo_login_passcode_is_required_and_limited()
    {
        var properties = await PropertiesOfAsync("DemoLoginRequest");

        Assert.Equal((1, 200), Lengths(properties["passcode"]!));
    }

    private async Task<JsonObject> PropertiesOfAsync(string schema)
    {
        var document = JsonNode.Parse(await Client.GetStringAsync("/openapi/v1.json", Ct))!;
        return document["components"]!["schemas"]![schema]!["properties"]!.AsObject();
    }

    private static (int? Min, int? Max) Lengths(JsonNode property) =>
        ((int?)property["minLength"], (int?)property["maxLength"]);
}
