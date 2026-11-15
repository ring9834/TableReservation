using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Reservation.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPosDelivery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "last_error",
                table: "pos_messages",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "next_attempt_at",
                table: "pos_messages",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "queued_at",
                table: "pos_messages",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.CreateTable(
                name: "mock_pos_open_tables",
                columns: table => new
                {
                    table_id = table.Column<Guid>(type: "uuid", nullable: false),
                    venue_id = table.Column<Guid>(type: "uuid", nullable: false),
                    booking_id = table.Column<Guid>(type: "uuid", nullable: false),
                    opened_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_mock_pos_open_tables", x => x.table_id);
                });

            migrationBuilder.CreateTable(
                name: "mock_pos_received",
                columns: table => new
                {
                    idempotency_key = table.Column<string>(type: "text", nullable: false),
                    received_order = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    venue_id = table.Column<Guid>(type: "uuid", nullable: false),
                    message_type = table.Column<string>(type: "text", nullable: false),
                    booking_id = table.Column<Guid>(type: "uuid", nullable: true),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_mock_pos_received", x => x.idempotency_key);
                });

            migrationBuilder.CreateTable(
                name: "mock_pos_state",
                columns: table => new
                {
                    id = table.Column<short>(type: "smallint", nullable: false),
                    online = table.Column<bool>(type: "boolean", nullable: false),
                    changed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_mock_pos_state", x => x.id);
                    table.CheckConstraint("ck_mock_pos_state_singleton", "id = 1");
                });

            migrationBuilder.CreateTable(
                name: "pos_venue_sequences",
                columns: table => new
                {
                    venue_id = table.Column<Guid>(type: "uuid", nullable: false),
                    last_sequence = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_pos_venue_sequences", x => x.venue_id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_pos_messages_outbound_open",
                table: "pos_messages",
                columns: new[] { "venue_id", "venue_sequence" },
                filter: "direction = 'Outbound' AND status IN ('Waiting', 'Failed')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "mock_pos_open_tables");

            migrationBuilder.DropTable(
                name: "mock_pos_received");

            migrationBuilder.DropTable(
                name: "mock_pos_state");

            migrationBuilder.DropTable(
                name: "pos_venue_sequences");

            migrationBuilder.DropIndex(
                name: "ix_pos_messages_outbound_open",
                table: "pos_messages");

            migrationBuilder.DropColumn(
                name: "last_error",
                table: "pos_messages");

            migrationBuilder.DropColumn(
                name: "next_attempt_at",
                table: "pos_messages");

            migrationBuilder.DropColumn(
                name: "queued_at",
                table: "pos_messages");
        }
    }
}
