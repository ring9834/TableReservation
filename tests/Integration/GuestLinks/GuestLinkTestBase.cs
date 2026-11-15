using System.Net;
using Microsoft.EntityFrameworkCore;
using Reservation.Api.Features.GuestBookings;
using Reservation.Api.Features.Reminders;
using Reservation.Domain.Bookings;
using Reservation.Domain.Messaging;
using Reservation.Tests.Integration.Payments;
using Reservation.Tests.Integration.Support;

namespace Reservation.Tests.Integration.GuestLinks;

/// <summary>
/// Reminders and guest links through the API (M10a, Design §7.3): the seed at 18:25, where #10 (GDB3K7, party of 2,
/// English, no deposit) and #11 (GDC4L8, party of 6, Chinese, deposit captured) are the only bookings a reminder may
/// go to. Payments are recorded by <see cref="PaymentTestBase.Payments"/>.
/// </summary>
public abstract class GuestLinkTestBase(PostgresFixture postgres) : PaymentTestBase(postgres)
{
    public const string ConfirmsRef = "GDB3K7";
    public const string CancelsRef = "GDC4L8";
    public const string GuestPath = "/api/v1/guest/bookings";

    private protected async Task<SendRemindersResponse> SendRemindersAsync()
    {
        var presenter = await PresenterAsync();
        return await ReadAsync<SendRemindersResponse>(await PostAsync(presenter, "/api/v1/demo/reminders/send-now", new { }));
    }

    protected async Task<Booking> ByReferenceAsync(string reference)
    {
        await using var db = Database.Context();
        return (await db.Bookings.AsNoTracking().ToListAsync(Ct)).Single(b => b.Reference.Value == reference);
    }

    /// <summary>The token of the given action ("confirm", "cancel", "manage") on the booking's only message of that purpose.</summary>
    protected async Task<string> TokenAsync(string reference, MessagePurpose purpose, string kind)
    {
        var booking = await ByReferenceAsync(reference);
        var message = Assert.Single(await MessagesAboutAsync(booking.Id.Value), m => m.Purpose == purpose);
        return TokenOf(Assert.Single(message.Actions, a => a.Kind == kind).Url);
    }

    protected static string TokenOf(string url)
    {
        const string Prefix = ApiFactory.GuestLinkBaseUrl + "/manage/?token=";
        Assert.StartsWith(Prefix, url, StringComparison.Ordinal);
        return url[Prefix.Length..];
    }

    protected Task<HttpResponseMessage> GetGuestAsync(string token) =>
        NewClient().GetAsync($"{GuestPath}?token={Uri.EscapeDataString(token)}", Ct);

    protected Task<HttpResponseMessage> PostGuestAsync(string action, string? token) =>
        PostAsync(NewClient(), $"{GuestPath}/{action}", new { token });

    private protected async Task<GuestBookingResponse> ReadGuestAsync(string token) =>
        await ReadAsync<GuestBookingResponse>(await GetGuestAsync(token));

    private protected async Task<GuestActionResponse> ActAsync(string action, string token) =>
        await ReadAsync<GuestActionResponse>(await PostGuestAsync(action, token));

    protected static void AssertGuestHeaders(HttpResponseMessage response)
    {
        Assert.Equal("no-referrer", Assert.Single(response.Headers.GetValues("Referrer-Policy")));
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString() ?? string.Empty, StringComparison.Ordinal);
    }

    protected static HttpStatusCode Gone => HttpStatusCode.Gone;
}
