using Reservation.Domain.Bookings;
using Reservation.Domain.Common;
using Reservation.Domain.Venues;

namespace Reservation.Tests.Unit.Support;

/// <summary>Golden Dragon test data with the Requirements defaults (BR-01 to BR-07).</summary>
public static class TestData
{
    /// <summary>Thursday 19 November 2026, 10:00 in Auckland (NZDT, UTC+13): when bookings are made.</summary>
    public static readonly DateTimeOffset Created = new(2026, 11, 18, 21, 0, 0, TimeSpan.Zero);

    /// <summary>Friday 20 November 2026, 19:00 in Auckland: the booking start, 33 hours after <see cref="Created"/>.</summary>
    public static readonly DateTimeOffset Start = new(2026, 11, 20, 6, 0, 0, TimeSpan.Zero);

    public static readonly TableId Table2 = new(Guid.Parse("00000000-0000-7000-8000-000000000002"));
    public static readonly TableId Table4 = new(Guid.Parse("00000000-0000-7000-8000-000000000004"));
    public static readonly TableId Table6 = new(Guid.Parse("00000000-0000-7000-8000-000000000006"));
    public static readonly TableId Table8 = new(Guid.Parse("00000000-0000-7000-8000-000000000008"));

    public static readonly GuestId GuestId = new(Guid.Parse("00000000-0000-7000-8000-0000000000a1"));

    public static readonly Actor Host = Actor.ForStaff("demo-host", "Host");

    public static Actor GuestActor => Actor.ForGuest(GuestId);

    public static VenueSettings Settings() => new(
        SlotMinutes: 15,
        ServiceStart: new TimeOnly(17, 0),
        LastBooking: new TimeOnly(20, 30),
        TurnTimes: [new TurnTimeRule(1, 2, 75), new TurnTimeRule(3, 4, 90), new TurnTimeRule(5, 8, 120)],
        PacingCoversPerSlot: 12,
        MaxOnlinePartySize: 8,
        Deposit: new DepositRule(6, 2000),
        Cancellation: new CancellationRule(2, RefundAmount.Full, RefundAmount.Full),
        NotifiedTimeoutMinutes: 10,
        PaymentHoldMinutes: 10,
        LateBillMatchMinutes: 30);

    public static readonly PersonName ContactName = PersonName.Create("Test Guest").Value;

    public static Venue Venue()
    {
        var position = new FloorPosition(0, 0, 0, TableShape.Round, 1, 1);
        var area = new Area(new AreaId(Guid.CreateVersion7(Created)), "Main", "主厅", 1,
        [
            new Table(Table2, "T2", 2, position),
            new Table(Table4, "T4", 4, position),
            new Table(Table6, "T6", 6, position),
            new Table(Table8, "T8", 8, position),
        ]);

        return Domain.Venues.Venue.Create(
            new VenueId(Guid.CreateVersion7(Created)), "Golden Dragon", "Pacific/Auckland", Money.Nzd, Settings(), [area]).Value;
    }

    public static BookingReference Reference() => BookingReference.Parse("GD7K4P").Value;

    public static BookingBill Bill(DateTimeOffset closedAt, string posBillId = "B-1001", long cents = 18_500) =>
        new(posBillId, Money.FromNzd(cents), null, closedAt, new PosMessageId(Guid.CreateVersion7(closedAt)));
}
