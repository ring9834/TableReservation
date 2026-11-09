using Reservation.Domain.Bookings;
using Reservation.Domain.Common;
using Reservation.Domain.Venues;

namespace Reservation.Infrastructure.Demo;

/// <summary>
/// The fictional demo venue (Requirements §0, §10; Design §17). IDs are fixed so the demo session,
/// tests and the reset always point at the same venue and tables.
/// </summary>
public static class GoldenDragon
{
    public const string Name = "Golden Dragon";
    public const string TimeZoneId = "Pacific/Auckland";

    /// <summary>Demo "now" after Reset: the DemoClock is fixed here (Design §17, M11).</summary>
    public static readonly TimeOnly DemoTime = new(18, 25);

    public static readonly VenueId VenueId = new(Guid.Parse("0193a7d0-0000-7000-8000-000000000001"));
    public static readonly AreaId MainRoomId = new(Guid.Parse("0193a7d0-0000-7000-8000-0000000000a1"));
    public static readonly AreaId WindowId = new(Guid.Parse("0193a7d0-0000-7000-8000-0000000000a2"));

    /// <summary>Main room: T1–T3 (2 seats), T4–T6 (4), T7–T9 (6), T10 (8). Window: W1–W2 (2), W3–W4 (4).</summary>
    public static readonly IReadOnlyList<TableSpec> Tables =
    [
        new("T1", 2, MainRoomId, new FloorPosition(80, 80, 0, TableShape.Round, 60, 60)),
        new("T2", 2, MainRoomId, new FloorPosition(200, 80, 0, TableShape.Round, 60, 60)),
        new("T3", 2, MainRoomId, new FloorPosition(320, 80, 0, TableShape.Round, 60, 60)),
        new("T4", 4, MainRoomId, new FloorPosition(80, 220, 0, TableShape.Rect, 90, 90)),
        new("T5", 4, MainRoomId, new FloorPosition(220, 220, 0, TableShape.Rect, 90, 90)),
        new("T6", 4, MainRoomId, new FloorPosition(360, 220, 0, TableShape.Rect, 90, 90)),
        new("T7", 6, MainRoomId, new FloorPosition(80, 380, 0, TableShape.Rect, 140, 90)),
        new("T8", 6, MainRoomId, new FloorPosition(260, 380, 0, TableShape.Rect, 140, 90)),
        new("T9", 6, MainRoomId, new FloorPosition(440, 380, 0, TableShape.Rect, 140, 90)),
        new("T10", 8, MainRoomId, new FloorPosition(500, 180, 0, TableShape.Round, 140, 140)),
        new("W1", 2, WindowId, new FloorPosition(700, 80, 0, TableShape.Round, 60, 60)),
        new("W2", 2, WindowId, new FloorPosition(700, 180, 0, TableShape.Round, 60, 60)),
        new("W3", 4, WindowId, new FloorPosition(700, 290, 90, TableShape.Rect, 90, 90)),
        new("W4", 4, WindowId, new FloorPosition(700, 410, 90, TableShape.Rect, 90, 90)),
    ];

    /// <summary>
    /// Tonight's bookings (Design §17 seed table). Built for the demo script at 18:25: every table of 4+ seats
    /// is taken for a walk-in of 4 (step 3), T4 frees on "Bill paid" (step 4), #10 and #11 get reminders (step 5).
    /// </summary>
    public static readonly IReadOnlyList<BookingSpec> Bookings =
    [
        new(1, "GD2A4K", new TimeOnly(17, 30), 4, BookingChannel.Phone, SeedStatus.Seated, "T4"),
        new(2, "GD3B7M", new TimeOnly(17, 30), 5, BookingChannel.Phone, SeedStatus.Seated, "T9"),
        new(3, "GD4C8N", new TimeOnly(17, 45), 3, BookingChannel.Online, SeedStatus.Seated, "W3"),
        new(4, "GD5D9P", new TimeOnly(18, 0), 7, BookingChannel.Phone, SeedStatus.Seated, "T10", Occasion: "Birthday"),
        new(5, "GD6E2Q", new TimeOnly(18, 15), 4, BookingChannel.Online, SeedStatus.Seated, "W4"),
        new(6, "GD7F3R", new TimeOnly(18, 45), 4, BookingChannel.Online, SeedStatus.Confirmed, "T5"),
        new(7, "GD8G4S", new TimeOnly(19, 0), 6, BookingChannel.Online, SeedStatus.Booked, "T7"),
        new(8, "GD9H5T", new TimeOnly(19, 0), 6, BookingChannel.Phone, SeedStatus.Booked, "T8"),
        new(9, "GDA2J6", new TimeOnly(19, 15), 4, BookingChannel.Online, SeedStatus.Booked, "T6"),
        new(10, "GDB3K7", new TimeOnly(20, 30), 2, BookingChannel.Online, SeedStatus.Booked, "W1", Occasion: "Anniversary"),
        new(11, "GDC4L8", new TimeOnly(20, 30), 6, BookingChannel.Online, SeedStatus.Booked, "T9"),
    ];

    /// <summary>One guest per booking, in the same order. Mobile numbers are fictional (+64 21 0xx xxxx).</summary>
    public static readonly IReadOnlyList<GuestSpec> Guests =
    [
        new("王伟 Wang Wei", "+64 21 011 0001", Language.Zh),
        new("Sarah Thompson", "+64 21 011 0002", Language.En),
        new("李娜 Li Na", "+64 21 011 0003", Language.Zh),
        new("James Wilson", "+64 21 011 0004", Language.En),
        new("陈美玲 Chen Meiling", "+64 21 011 0005", Language.Zh),
        new("Aroha Ngata", "+64 21 011 0006", Language.En),
        new("张晓燕 Zhang Xiaoyan", "+64 21 011 0007", Language.Zh),
        new("Michael Brown", "+64 21 011 0008", Language.En),
        new("刘洋 Liu Yang", "+64 21 011 0009", Language.Zh),
        new("Emma Clarke", "+64 21 011 0010", Language.En),
        new("黄建国 Huang Jianguo", "+64 21 011 0011", Language.Zh),
    ];

    /// <summary>Requirements §0 and BR-01 to BR-10.</summary>
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

    public static TableId TableId(string name)
    {
        var index = Tables.ToList().FindIndex(t => t.Name == name);
        return index < 0
            ? throw new ArgumentException($"No table {name}.", nameof(name))
            : new TableId(Guid.Parse($"0193a7d0-0000-7000-8000-{index + 1:x12}"));
    }

    public static TimeZoneInfo TimeZone() => TimeZoneInfo.FindSystemTimeZoneById(TimeZoneId);

    /// <summary>The service date "tonight": today's date in Auckland.</summary>
    public static DateOnly ServiceDate(DateTimeOffset now) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, TimeZone()).DateTime);

    /// <summary>A local Auckland date and time as a UTC instant.</summary>
    public static DateTimeOffset At(DateOnly date, TimeOnly time) =>
        new(TimeZoneInfo.ConvertTimeToUtc(date.ToDateTime(time, DateTimeKind.Unspecified), TimeZone()));

    public sealed record TableSpec(string Name, int Seats, AreaId Area, FloorPosition Position);

    public sealed record GuestSpec(string Name, string Mobile, Language Language);

    public sealed record BookingSpec(
        int Number, string Reference, TimeOnly Start, int PartySize, BookingChannel Channel, SeedStatus Status, string Table,
        string? Occasion = null);

    public enum SeedStatus
    {
        Booked,
        Confirmed,
        Seated,
    }
}
