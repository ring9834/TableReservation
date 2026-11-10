using System.Net;
using Reservation.Api.Features.Bookings;
using Reservation.Tests.Integration.Support;

namespace Reservation.Tests.Integration.Api;

/// <summary>
/// ADR-004 "What is never logged": a sample booking goes through the API and the captured log output (messages,
/// properties, scopes, exceptions) must not contain any of its personal data or the request body.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class LogScrubbingTests(PostgresFixture postgres) : ApiTestBase(postgres)
{
    private const string Name = "Zyxa Quillfeather";
    private const string Mobile = "021 011 0166";
    private const string Email = "zyxa.quillfeather@example.org";
    private const string Notes = "Scrub-note-7f3a";
    private const string Allergy = "Scrub-allergy-9c1e";
    private const string Occasion = "Scrub-occasion-2b8d";

    [Fact]
    public async Task Booking_requests_leave_no_personal_data_in_the_logs()
    {
        var body = BookingBody(DemoTime.Tonight, 19, 30, 2, Name, Mobile, Email, Notes, Allergy, Occasion);
        await ReadAsync<CreateBookingResponse>(await PostAsync(Client, Venue + "/bookings", body, "scrub-key"), HttpStatusCode.Created);
        await ReadAsync<CreateBookingResponse>(await PostAsync(Client, Venue + "/bookings", body, "scrub-key"), HttpStatusCode.Created);
        await PostAsync(Client, Venue + "/bookings", BookingBody(DemoTime.Tonight, 19, 30, 2, Name, Mobile, "not-an-email " + Email, Notes));
        await SignInAsync(Client);
        await ReadAsync<BookingListResponse>(await Client.GetAsync(Venue + "/bookings", Ct)); // names in the response only

        var log = string.Join('\n', Factory.Logs.Lines.Select(l => l.Text));
        Assert.Contains("HTTP POST /api/v1/venues/{venueId:guid}/bookings responded 201", log, StringComparison.Ordinal);
        string[] secrets =
        [
            Name, "Quillfeather", Email, Notes, Allergy, Occasion, ApiFactory.HostPasscode,
            "021 011 0166", "0210110166", "+64210110166", "210110166", "__Host-demo-session",
        ];
        foreach (var secret in secrets)
        {
            Assert.DoesNotContain(secret, log, StringComparison.OrdinalIgnoreCase);
        }
    }
}
