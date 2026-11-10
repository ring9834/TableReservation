using Reservation.Domain.Common;
using Reservation.Domain.Guests;
using Reservation.Tests.Unit.Support;

namespace Reservation.Tests.Unit.Guests;

/// <summary>Guest aggregate (Design §2.2).</summary>
public class GuestTests
{
    private static readonly VenueId _venueId = new(Guid.CreateVersion7(TestData.Created));

    [Fact]
    public void Create_StartsWithNoVisitsAndRaisesNothing()
    {
        var guest = NewGuest();

        Assert.Equal("Mei Lin", guest.Name.Value);
        Assert.Equal("+64211234567", guest.Mobile.E164);
        Assert.Equal(Language.Zh, guest.Language);
        Assert.Equal(0, guest.VisitCount);
        Assert.Null(guest.LastBillTotal);
        Assert.Null(guest.Email);
        EventAssert.NoneRaised(guest, 0);
    }

    [Fact]
    public void UseLanguage_ChangesOnlyTheLanguageAndRaisesNothing()
    {
        var guest = NewGuest();

        guest.UseLanguage(Language.En);

        Assert.Equal(Language.En, guest.Language);
        Assert.Equal("Mei Lin", guest.Name.Value);
        Assert.Equal("+64211234567", guest.Mobile.E164);
        EventAssert.NoneRaised(guest, 0);
    }

    [Fact]
    public void UseLanguage_UnknownLanguage_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => NewGuest().UseLanguage((Language)9));
    }

    [Fact]
    public void RecordVisit_CountsTheVisitAndKeepsTheLastBill_AC66()
    {
        var guest = NewGuest();

        guest.RecordVisit(Money.FromNzd(18_500), TestData.Start.AddHours(1));
        guest.RecordVisit(Money.FromNzd(9_900), TestData.Start.AddDays(7));

        Assert.Equal(2, guest.VisitCount);
        Assert.Equal(Money.FromNzd(9_900), guest.LastBillTotal);
        Assert.Equal(TestData.Start.AddDays(7), guest.LastVisitAt);
    }

    private static Guest NewGuest() => Guest.Create(
        _venueId, PersonName.Create("  Mei Lin ").Value, PhoneNumber.Create("021 123 4567").Value,
        email: " ", Language.Zh, allergyNotes: null, TestData.Created);
}
