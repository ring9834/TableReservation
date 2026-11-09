using Reservation.Domain.Bookings;
using Reservation.Domain.Common;
using Reservation.Tests.Unit.Support;

namespace Reservation.Tests.Unit.Common;

/// <summary>AggregateRoot: Raise stamps LastEventSequence (Design §2.2).</summary>
public class AggregateRootTests
{
    [Fact]
    public void Raise_StampsConsecutiveSequenceNumbers_AndClearKeepsTheCounter()
    {
        var booking = BookingFactory.Create();
        Assert.Equal(1, booking.LastEventSequence);

        booking.ClearDomainEvents();
        booking.Confirm(TestData.GuestActor, TestData.Created.AddHours(1));

        Assert.Equal(2, booking.LastEventSequence);
        var raised = Assert.Single(booking.DomainEvents);
        Assert.Equal(2, raised.Sequence);
        Assert.Equal(0u, booking.Version);
    }

    [Fact]
    public void EventIds_AreUuidVersion7()
    {
        var booking = BookingFactory.Create();

        Assert.Equal(7, booking.DomainEvents[0].Event.EventId.Version);
        Assert.Equal(7, booking.Id.Value.Version);
    }

    [Fact]
    public void EveryEventType_HasMetadata()
    {
        var eventTypes = typeof(DomainEvent).Assembly.GetTypes()
            .Where(t => t.IsSubclassOf(typeof(DomainEvent)) && !t.IsAbstract)
            .ToList();

        Assert.Equal(13, eventTypes.Count);
        Assert.All(eventTypes, t =>
        {
            var metadata = (EventMetadataAttribute?)Attribute.GetCustomAttribute(t, typeof(EventMetadataAttribute));
            Assert.NotNull(metadata);
            Assert.Equal(t.Name, metadata.Name);
            Assert.Equal(1, metadata.Version);
            Assert.False(string.IsNullOrEmpty(metadata.AggregateType));
        });
    }
}

/// <summary>Result and Error (ADR-004).</summary>
public class ResultTests
{
    [Fact]
    public void InvalidTransition_IsAConflict()
    {
        Assert.Equal(ErrorCodes.InvalidTransition, Error.InvalidTransition.Code);
        Assert.Equal(ErrorKind.Conflict, Error.InvalidTransition.Kind);
        Assert.Equal("errors.invalid_transition", Error.InvalidTransition.MessageKey);
    }

    [Fact]
    public void Value_OnFailure_Throws()
    {
        Result<int> result = Error.Rule(ErrorCodes.PartyTooLarge);

        Assert.True(result.IsFailure);
        Assert.Throws<InvalidOperationException>(() => result.Value);
    }

    [Fact]
    public void Validation_PutsTheRuleCodeInFields()
    {
        var error = Error.Validation("partySize", "party_size_out_of_range");

        Assert.Equal(ErrorCodes.ValidationFailed, error.Code);
        Assert.Equal(["party_size_out_of_range"], error.Fields!["partySize"]);
    }
}

/// <summary>Value objects (Design §2.3).</summary>
public class ValueObjectTests
{
    [Theory]
    [InlineData(1, true)]
    [InlineData(20, true)]
    [InlineData(0, false)]
    [InlineData(21, false)]
    public void PartySize_IsOneToTwenty(int value, bool valid)
    {
        Assert.Equal(valid, PartySize.Create(value).IsSuccess);
    }

    [Theory]
    [InlineData("021 123 4567", "+64211234567")]
    [InlineData("0221234567", "+64221234567")]
    [InlineData("+64 27 123 4567", "+64271234567")]
    public void PhoneNumber_ParsesNzMobilesToE164_BR12(string input, string expected)
    {
        Assert.Equal(expected, PhoneNumber.Create(input).Value.E164);
    }

    [Theory]
    [InlineData("09 123 4567")]
    [InlineData("12345")]
    [InlineData("")]
    [InlineData("not a number")]
    public void PhoneNumber_RejectsLandlinesAndJunk_BR12(string input)
    {
        EventAssert.Failed(PhoneNumber.Create(input), ErrorCodes.ValidationFailed, ErrorKind.Validation);
    }

    [Fact]
    public void PhoneNumber_Masked_ShowsOnlyCountryCodeAndLastThreeDigits()
    {
        var masked = PhoneNumber.Create("021 123 4567").Value.Masked();

        Assert.Equal("+64******567", masked);
        Assert.DoesNotContain("1234", masked, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("  Mei  ", true)]
    [InlineData("   ", false)]
    public void PersonName_IsTrimmedAndRequired(string input, bool valid)
    {
        var result = PersonName.Create(input);

        Assert.Equal(valid, result.IsSuccess);
        if (valid)
        {
            Assert.Equal("Mei", result.Value.Value);
        }
    }

    [Fact]
    public void PersonName_IsAtMost80Characters()
    {
        Assert.True(PersonName.Create(new string('a', 80)).IsSuccess);
        Assert.False(PersonName.Create(new string('a', 81)).IsSuccess);
    }

    [Fact]
    public void Money_AddsSameCurrency_AndRefusesMixedCurrency()
    {
        Assert.Equal(Money.FromNzd(300), Money.FromNzd(100) + Money.FromNzd(200));
        Assert.Throws<InvalidOperationException>(() => Money.FromNzd(100) + new Money(100, "AUD"));
        Assert.False(Money.Create(-1, Money.Nzd).IsSuccess);
        Assert.False(Money.Create(1, "nzd").IsSuccess);
    }

    [Fact]
    public void TimeRange_IsHalfOpenAndRequiresEndAfterStart()
    {
        var range = new TimeRange(TestData.Start, TestData.Start.AddMinutes(90));
        var next = new TimeRange(TestData.Start.AddMinutes(90), TestData.Start.AddMinutes(180));

        Assert.True(range.Contains(TestData.Start));
        Assert.False(range.Contains(TestData.Start.AddMinutes(90)));
        Assert.False(range.Overlaps(next));
        Assert.True(range.Overlaps(new TimeRange(TestData.Start.AddMinutes(89), TestData.Start.AddMinutes(100))));
        Assert.Throws<ArgumentException>(() => new TimeRange(TestData.Start, TestData.Start));
        Assert.False(TimeRange.Create(TestData.Start, TestData.Start.AddMinutes(-1)).IsSuccess);
    }

    [Fact]
    public void TimeRange_IsStoredInUtc()
    {
        var local = new DateTimeOffset(2026, 11, 20, 19, 0, 0, TimeSpan.FromHours(13));

        var range = TimeRange.FromStart(local, TimeSpan.FromMinutes(75));

        Assert.Equal(TimeSpan.Zero, range.Start.Offset);
        Assert.Equal(TestData.Start, range.Start);
    }

    [Theory]
    [InlineData("GD7K4P", true)]
    [InlineData("gd7k4p", true)]
    [InlineData("GD0K4P", false)]
    [InlineData("GDIK4P", false)]
    [InlineData("GD7K4", false)]
    public void BookingReference_UsesSixUnambiguousCharacters(string input, bool valid)
    {
        Assert.Equal(valid, BookingReference.Parse(input).IsSuccess);
    }

    [Fact]
    public void BookingReference_Generate_OnlyUsesTheAlphabet()
    {
        var random = new Random(42);

        for (var i = 0; i < 100; i++)
        {
            var reference = BookingReference.Generate(random).Value;
            Assert.Equal(6, reference.Length);
            Assert.All(reference, c => Assert.Contains(c, BookingReference.Alphabet));
        }
    }
}
