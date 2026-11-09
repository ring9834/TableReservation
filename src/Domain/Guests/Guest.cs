using Reservation.Domain.Common;

namespace Reservation.Domain.Guests;

/// <summary>Guest aggregate (Design §2.2). Shared by bookings and the waitlist; matched by mobile number.</summary>
public sealed class Guest : AggregateRoot<GuestId>
{
    /// <summary>For EF Core only.</summary>
    private Guest()
    {
        Name = null!;
        Mobile = null!;
    }

    private Guest(GuestId id, VenueId venueId, PersonName name, PhoneNumber mobile, string? email, Language language, string? allergyNotes)
    {
        Id = id;
        VenueId = venueId;
        Name = name;
        Mobile = mobile;
        Email = email;
        Language = language;
        AllergyNotes = allergyNotes;
    }

    public VenueId VenueId { get; }

    public PersonName Name { get; private set; }

    public PhoneNumber Mobile { get; private set; }

    public string? Email { get; private set; }

    public Language Language { get; private set; }

    public string? AllergyNotes { get; private set; }

    public int VisitCount { get; private set; }

    public Money? LastBillTotal { get; private set; }

    public DateTimeOffset? LastVisitAt { get; private set; }

    public static Guest Create(
        VenueId venueId, PersonName name, PhoneNumber mobile, string? email, Language language, string? allergyNotes, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(mobile);
        return new Guest(GuestId.New(now), venueId, name, mobile, NullIfBlank(email), language, NullIfBlank(allergyNotes));
    }

    /// <summary>A visit with a bill from the POS (AC6.6). The handler's inbox row makes this run once per event.</summary>
    public void RecordVisit(Money billTotal, DateTimeOffset at)
    {
        VisitCount++;
        LastBillTotal = billTotal;
        LastVisitAt = at;
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
