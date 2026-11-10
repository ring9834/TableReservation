using FluentValidation;
using Reservation.Api.Auth;
using Reservation.Api.Features.Bookings;
using Reservation.Domain.Common;
using Reservation.Tests.Unit.Support;

namespace Reservation.Tests.Unit.Bookings;

/// <summary>Every request validator (ADR-004 "Every validator has unit tests"): each rule reports its own code.</summary>
public sealed class ValidatorTests
{
    private static readonly CreateBookingRequest Valid = new(
        TestData.Start, 2, "Lin Meihua", "021 011 0199", Language.En);

    private static string[] Codes<T>(AbstractValidator<T> validator, T request, string property) =>
        validator.Validate(request).Errors.Where(e => e.PropertyName == property).Select(e => e.ErrorCode).ToArray();

    [Fact]
    public void CreateBooking_ValidRequest_Passes()
    {
        Assert.True(new CreateBookingRequestValidator().Validate(Valid with { Email = "lin@example.org", Notes = "Window" }).IsValid);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(21)]
    public void CreateBooking_PartySizeOutside1To20_IsPartySizeOutOfRange(int partySize)
    {
        Assert.Equal(["party_size_out_of_range"], Codes(new CreateBookingRequestValidator(), Valid with { PartySize = partySize }, "PartySize"));
    }

    [Fact]
    public void CreateBooking_MissingStart_IsStartsAtRequired()
    {
        Assert.Equal(["starts_at_required"], Codes(new CreateBookingRequestValidator(), Valid with { StartsAt = default }, "StartsAt"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void CreateBooking_BlankName_IsNameLength(string name)
    {
        Assert.Equal(["name_length"], Codes(new CreateBookingRequestValidator(), Valid with { Name = name }, "Name"));
    }

    [Fact]
    public void CreateBooking_NameOver80Characters_IsNameLength()
    {
        Assert.Equal(["name_length"], Codes(new CreateBookingRequestValidator(), Valid with { Name = new string('a', 81) }, "Name"));
    }

    [Theory]
    [InlineData("12345", "mobile_invalid")]
    [InlineData("", "mobile_invalid")]
    [InlineData("09 300 1234", "mobile_not_mobile")]
    public void CreateBooking_BadMobile_ReportsTheDomainCode_BR12(string mobile, string code)
    {
        Assert.Equal([code], Codes(new CreateBookingRequestValidator(), Valid with { Mobile = mobile }, "Mobile"));
    }

    [Fact]
    public void CreateBooking_BadEmail_IsEmailInvalid()
    {
        Assert.Equal(["email_invalid"], Codes(new CreateBookingRequestValidator(), Valid with { Email = "nope" }, "Email"));
    }

    [Fact]
    public void CreateBooking_LongFreeText_IsTooLong()
    {
        var request = Valid with { Notes = new string('n', 501), AllergyNotes = new string('a', 501), Occasion = new string('o', 51) };
        var validator = new CreateBookingRequestValidator();

        Assert.Equal(["notes_too_long"], Codes(validator, request, "Notes"));
        Assert.Equal(["allergy_notes_too_long"], Codes(validator, request, "AllergyNotes"));
        Assert.Equal(["occasion_too_long"], Codes(validator, request, "Occasion"));
    }

    [Fact]
    public void CreateBooking_UnknownLanguage_IsLanguageInvalid()
    {
        Assert.Equal(["language_invalid"], Codes(new CreateBookingRequestValidator(), Valid with { Language = (Language)9 }, "Language"));
    }

    [Fact]
    public void Availability_MissingDateAndPartySize_AreRequired()
    {
        var errors = new AvailabilityQueryValidator().Validate(new AvailabilityQuery(null, null, null)).Errors;

        Assert.Contains(errors, e => e.PropertyName == "Date" && e.ErrorCode == "date_required");
        Assert.Contains(errors, e => e.PropertyName == "PartySize" && e.ErrorCode == "party_size_required");
    }

    [Fact]
    public void Availability_PartySizeOver20_IsOutOfRange()
    {
        Assert.Equal(
            ["party_size_out_of_range"],
            Codes(new AvailabilityQueryValidator(), new AvailabilityQuery(new DateOnly(2026, 11, 20), 21, null), "PartySize"));
    }

    [Fact]
    public void BookingVersion_Zero_IsVersionRequired()
    {
        Assert.Equal(["version_required"], Codes(new BookingVersionRequestValidator(), new BookingVersionRequest(0), "Version"));
        Assert.True(new BookingVersionRequestValidator().Validate(new BookingVersionRequest(42)).IsValid);
    }

    [Fact]
    public void DemoLogin_EmptyOrHugePasscode_IsRefused()
    {
        Assert.Equal(["passcode_required"], Codes(new DemoLoginRequestValidator(), new DemoLoginRequest(""), "Passcode"));
        Assert.Equal(["passcode_too_long"], Codes(new DemoLoginRequestValidator(), new DemoLoginRequest(new string('p', 201)), "Passcode"));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("abc-123_XYZ", true)]
    [InlineData("", false)]
    [InlineData("has space", false)]
    [InlineData("ключ", false)]
    public void IdempotencyKey_IsPrintableAsciiUpTo100Characters(string? key, bool valid)
    {
        Assert.Equal(valid, IdempotencyKey.IsValid(key));
        Assert.False(IdempotencyKey.IsValid(new string('k', 101)));
    }
}
