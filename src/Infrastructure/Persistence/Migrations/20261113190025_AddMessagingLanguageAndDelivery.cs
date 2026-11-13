using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Reservation.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// M7a (Design §7.2). <c>bookings.language</c> and <c>waitlist_entries.language</c>: the language of each booking's
    /// or entry's messages, backfilled from the guest (<c>En</c> for a booking without one), then required.
    /// <c>messages.delivery_due_at</c>: when the worker marks a sent message delivered (infrastructure clock), with a partial
    /// index for the worker's claim; indexes on <c>subject_id</c> (waitlist read) and <c>(venue_id, created_at)</c> (message log).
    /// </summary>
    public partial class AddMessagingLanguageAndDelivery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "language",
                table: "bookings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "language",
                table: "waitlist_entries",
                type: "text",
                nullable: true);

            migrationBuilder.Sql(
                """
                UPDATE bookings b SET language = g.language FROM guests g WHERE b.guest_id = g.id AND b.language IS NULL;
                UPDATE bookings SET language = 'En' WHERE language IS NULL;
                UPDATE waitlist_entries w SET language = g.language FROM guests g WHERE w.guest_id = g.id AND w.language IS NULL;
                """);

            migrationBuilder.AlterColumn<string>(
                name: "language",
                table: "bookings",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "language",
                table: "waitlist_entries",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "delivery_due_at",
                table: "messages",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_messages_delivery_due",
                table: "messages",
                column: "delivery_due_at",
                filter: "status = 'Sent'");

            migrationBuilder.CreateIndex(
                name: "ix_messages_subject_id",
                table: "messages",
                column: "subject_id");

            migrationBuilder.CreateIndex(
                name: "ix_messages_venue_created",
                table: "messages",
                columns: new[] { "venue_id", "created_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_messages_delivery_due",
                table: "messages");

            migrationBuilder.DropIndex(
                name: "ix_messages_subject_id",
                table: "messages");

            migrationBuilder.DropIndex(
                name: "ix_messages_venue_created",
                table: "messages");

            migrationBuilder.DropColumn(
                name: "language",
                table: "waitlist_entries");

            migrationBuilder.DropColumn(
                name: "delivery_due_at",
                table: "messages");

            migrationBuilder.DropColumn(
                name: "language",
                table: "bookings");
        }
    }
}
