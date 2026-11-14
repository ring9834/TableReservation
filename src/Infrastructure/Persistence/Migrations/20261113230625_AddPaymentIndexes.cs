using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Reservation.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// M8a (Design §8.4). <c>ux_bookings_payment_intent_id</c>: <c>charge.refunded</c> finds the booking by its payment
    /// intent, one booking per intent. <c>ix_bookings_hold_expiry</c>: <c>PaymentHoldExpiryJob</c> reads only pending holds.
    /// </summary>
    public partial class AddPaymentIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_bookings_hold_expiry",
                table: "bookings",
                column: "hold_expires_at",
                filter: "status = 'PendingPayment'");

            migrationBuilder.CreateIndex(
                name: "ux_bookings_payment_intent_id",
                table: "bookings",
                column: "payment_intent_id",
                unique: true,
                filter: "payment_intent_id IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_bookings_hold_expiry",
                table: "bookings");

            migrationBuilder.DropIndex(
                name: "ux_bookings_payment_intent_id",
                table: "bookings");
        }
    }
}
