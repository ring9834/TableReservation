using Reservation.Domain.Common;

namespace Reservation.Api.Common;

/// <summary>Money on the wire: integer cents plus a currency code (ADR-004 conventions).</summary>
internal sealed record MoneyDto(long AmountCents, string Currency)
{
    public static MoneyDto From(Money money) => new(money.Cents, money.Currency);
}
