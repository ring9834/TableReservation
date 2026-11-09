using System;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;
using NpgsqlTypes;

#nullable disable

namespace Reservation.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:btree_gist", ",,");

            migrationBuilder.CreateTable(
                name: "audit_log",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.SerialColumn),
                    venue_id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_type = table.Column<string>(type: "text", nullable: false),
                    aggregate_id = table.Column<Guid>(type: "uuid", nullable: false),
                    actor = table.Column<JsonDocument>(type: "jsonb", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    summary = table.Column<JsonDocument>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_audit_log", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "inbox",
                columns: table => new
                {
                    consumer = table.Column<string>(type: "text", nullable: false),
                    message_id = table.Column<string>(type: "text", nullable: false),
                    processed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_inbox", x => new { x.consumer, x.message_id });
                });

            migrationBuilder.CreateTable(
                name: "messages",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    venue_id = table.Column<Guid>(type: "uuid", nullable: false),
                    guest_id = table.Column<Guid>(type: "uuid", nullable: false),
                    purpose = table.Column<string>(type: "text", nullable: false),
                    channel = table.Column<string>(type: "text", nullable: false),
                    language = table.Column<string>(type: "text", nullable: false),
                    body = table.Column<string>(type: "text", nullable: false),
                    subject_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    sent_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    delivered_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    attempts = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    next_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    provider_ref = table.Column<string>(type: "text", nullable: true),
                    last_error = table.Column<string>(type: "text", nullable: true),
                    actions = table.Column<string>(type: "jsonb", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    last_event_sequence = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_messages", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "outbox",
                columns: table => new
                {
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    venue_id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_type = table.Column<string>(type: "text", nullable: false),
                    event_version = table.Column<int>(type: "integer", nullable: false),
                    aggregate_type = table.Column<string>(type: "text", nullable: false),
                    aggregate_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sequence = table.Column<long>(type: "bigint", nullable: false),
                    correlation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    causation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    envelope = table.Column<JsonDocument>(type: "jsonb", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    next_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_error = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    dispatched_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_outbox", x => x.event_id);
                });

            migrationBuilder.CreateTable(
                name: "pos_messages",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    venue_id = table.Column<Guid>(type: "uuid", nullable: false),
                    direction = table.Column<string>(type: "text", nullable: false),
                    message_type = table.Column<string>(type: "text", nullable: false),
                    idempotency_key = table.Column<string>(type: "text", nullable: false),
                    source_event_id = table.Column<Guid>(type: "uuid", nullable: true),
                    payload = table.Column<JsonDocument>(type: "jsonb", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    venue_sequence = table.Column<long>(type: "bigint", nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    delivered_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    booking_id = table.Column<Guid>(type: "uuid", nullable: true),
                    unmatched_reason = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_pos_messages", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "venues",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    time_zone = table.Column<string>(type: "text", nullable: false),
                    currency = table.Column<string>(type: "char(3)", nullable: false, defaultValue: "NZD"),
                    settings = table.Column<string>(type: "jsonb", nullable: false),
                    settings_version = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    last_event_sequence = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_venues", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "areas",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    venue_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_areas", x => x.id);
                    table.ForeignKey(
                        name: "fk_areas_venues_venue_id",
                        column: x => x.venue_id,
                        principalTable: "venues",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "guests",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    venue_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    mobile_e164 = table.Column<string>(type: "text", nullable: false),
                    email = table.Column<string>(type: "text", nullable: true),
                    language = table.Column<string>(type: "text", nullable: false),
                    allergy_notes = table.Column<string>(type: "text", nullable: true),
                    visit_count = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    last_bill_cents = table.Column<long>(type: "bigint", nullable: true),
                    last_visit_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    last_event_sequence = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_guests", x => x.id);
                    table.ForeignKey(
                        name: "fk_guests_venues_venue_id",
                        column: x => x.venue_id,
                        principalTable: "venues",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "tables",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    seats = table.Column<int>(type: "integer", nullable: false),
                    position = table.Column<string>(type: "jsonb", nullable: false),
                    area_id = table.Column<Guid>(type: "uuid", nullable: false),
                    venue_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tables", x => x.id);
                    table.CheckConstraint("ck_tables_seats", "seats > 0");
                    table.ForeignKey(
                        name: "fk_tables_areas_area_id",
                        column: x => x.area_id,
                        principalTable: "areas",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_tables_venues_venue_id",
                        column: x => x.venue_id,
                        principalTable: "venues",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "bookings",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    venue_id = table.Column<Guid>(type: "uuid", nullable: false),
                    guest_id = table.Column<Guid>(type: "uuid", nullable: true),
                    reference = table.Column<string>(type: "text", nullable: false),
                    channel = table.Column<string>(type: "text", nullable: false),
                    party_size = table.Column<int>(type: "integer", nullable: false),
                    period = table.Column<NpgsqlRange<DateTime>>(type: "tstzrange", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    notes = table.Column<string>(type: "text", nullable: true),
                    occasion = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    hold_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    seated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    completion_source = table.Column<string>(type: "text", nullable: true),
                    cancel_reason = table.Column<string>(type: "text", nullable: true),
                    refund_decision = table.Column<string>(type: "text", nullable: true),
                    deposit_cents = table.Column<long>(type: "bigint", nullable: true),
                    deposit_status = table.Column<string>(type: "text", nullable: true),
                    payment_intent_id = table.Column<string>(type: "text", nullable: true),
                    refund_id = table.Column<string>(type: "text", nullable: true),
                    reminder = table.Column<string>(type: "jsonb", nullable: true),
                    terms = table.Column<string>(type: "jsonb", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    last_event_sequence = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_bookings", x => x.id);
                    table.CheckConstraint("ck_bookings_party_size", "party_size BETWEEN 1 AND 20");
                    table.ForeignKey(
                        name: "fk_bookings_guests_guest_id",
                        column: x => x.guest_id,
                        principalTable: "guests",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_bookings_venues_venue_id",
                        column: x => x.venue_id,
                        principalTable: "venues",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "waitlist_entries",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    venue_id = table.Column<Guid>(type: "uuid", nullable: false),
                    guest_id = table.Column<Guid>(type: "uuid", nullable: false),
                    party_size = table.Column<int>(type: "integer", nullable: false),
                    quoted_wait_minutes = table.Column<int>(type: "integer", nullable: false),
                    joined_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    notified_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    notified_table_id = table.Column<Guid>(type: "uuid", nullable: true),
                    ended_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    booking_id = table.Column<Guid>(type: "uuid", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    last_event_sequence = table.Column<long>(type: "bigint", nullable: false, defaultValue: 0L)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_waitlist_entries", x => x.id);
                    table.ForeignKey(
                        name: "fk_waitlist_entries_guests_guest_id",
                        column: x => x.guest_id,
                        principalTable: "guests",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "booking_bills",
                columns: table => new
                {
                    pos_bill_id = table.Column<string>(type: "text", nullable: false),
                    booking_id = table.Column<Guid>(type: "uuid", nullable: false),
                    total_cents = table.Column<long>(type: "bigint", nullable: false),
                    tip_cents = table.Column<long>(type: "bigint", nullable: true),
                    closed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    pos_message_id = table.Column<Guid>(type: "uuid", nullable: false),
                    venue_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_booking_bills", x => new { x.booking_id, x.pos_bill_id });
                    table.ForeignKey(
                        name: "fk_booking_bills_bookings_booking_id",
                        column: x => x.booking_id,
                        principalTable: "bookings",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "table_assignments",
                columns: table => new
                {
                    table_id = table.Column<Guid>(type: "uuid", nullable: false),
                    booking_id = table.Column<Guid>(type: "uuid", nullable: false),
                    period = table.Column<NpgsqlRange<DateTime>>(type: "tstzrange", nullable: false),
                    active = table.Column<bool>(type: "boolean", nullable: false),
                    venue_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_table_assignments", x => new { x.booking_id, x.table_id });
                    table.ForeignKey(
                        name: "fk_table_assignments_bookings_booking_id",
                        column: x => x.booking_id,
                        principalTable: "bookings",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_table_assignments_tables_table_id",
                        column: x => x.table_id,
                        principalTable: "tables",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_areas_venue_id",
                table: "areas",
                column: "venue_id");

            migrationBuilder.CreateIndex(
                name: "ix_audit_log_event_id",
                table: "audit_log",
                column: "event_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_booking_bills_one_per_booking",
                table: "booking_bills",
                column: "booking_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_booking_bills_venue_pos_bill",
                table: "booking_bills",
                columns: new[] { "venue_id", "pos_bill_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_bookings_guest_id",
                table: "bookings",
                column: "guest_id");

            migrationBuilder.CreateIndex(
                name: "ix_bookings_venue_id_reference",
                table: "bookings",
                columns: new[] { "venue_id", "reference" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_guests_venue_id_mobile_e164",
                table: "guests",
                columns: new[] { "venue_id", "mobile_e164" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_messages_queued",
                table: "messages",
                column: "next_attempt_at",
                filter: "status = 'Queued'");

            migrationBuilder.CreateIndex(
                name: "ix_outbox_aggregate_id_sequence",
                table: "outbox",
                columns: new[] { "aggregate_id", "sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_outbox_pending",
                table: "outbox",
                column: "next_attempt_at",
                filter: "status = 'Pending'");

            migrationBuilder.CreateIndex(
                name: "ix_pos_messages_idempotency_key",
                table: "pos_messages",
                column: "idempotency_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_pos_messages_venue_id_venue_sequence",
                table: "pos_messages",
                columns: new[] { "venue_id", "venue_sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_pos_unmatched",
                table: "pos_messages",
                columns: new[] { "venue_id", "created_at" },
                filter: "status = 'Unmatched'");

            migrationBuilder.CreateIndex(
                name: "ix_table_assignments_table_id",
                table: "table_assignments",
                column: "table_id");

            migrationBuilder.CreateIndex(
                name: "ix_tables_area_id",
                table: "tables",
                column: "area_id");

            migrationBuilder.CreateIndex(
                name: "ix_tables_venue_id",
                table: "tables",
                column: "venue_id");

            migrationBuilder.CreateIndex(
                name: "ix_waitlist_entries_guest_id",
                table: "waitlist_entries",
                column: "guest_id");

            // Not expressible in the EF model, so kept out of the snapshot (Design §12, §13).
            // One party per table at a time: overlapping active assignments fail with 23P01 (slot_taken).
            migrationBuilder.Sql(
                "ALTER TABLE table_assignments ADD CONSTRAINT ex_table_assignments_no_overlap " +
                "EXCLUDE USING gist (table_id WITH =, period WITH &&) WHERE (active);");

            migrationBuilder.Sql("CREATE INDEX ix_bookings_venue_day ON bookings (venue_id, lower(period));");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX IF EXISTS ix_bookings_venue_day;");
            migrationBuilder.Sql("ALTER TABLE table_assignments DROP CONSTRAINT IF EXISTS ex_table_assignments_no_overlap;");

            migrationBuilder.DropTable(
                name: "audit_log");

            migrationBuilder.DropTable(
                name: "booking_bills");

            migrationBuilder.DropTable(
                name: "inbox");

            migrationBuilder.DropTable(
                name: "messages");

            migrationBuilder.DropTable(
                name: "outbox");

            migrationBuilder.DropTable(
                name: "pos_messages");

            migrationBuilder.DropTable(
                name: "table_assignments");

            migrationBuilder.DropTable(
                name: "waitlist_entries");

            migrationBuilder.DropTable(
                name: "bookings");

            migrationBuilder.DropTable(
                name: "tables");

            migrationBuilder.DropTable(
                name: "guests");

            migrationBuilder.DropTable(
                name: "areas");

            migrationBuilder.DropTable(
                name: "venues");
        }
    }
}
