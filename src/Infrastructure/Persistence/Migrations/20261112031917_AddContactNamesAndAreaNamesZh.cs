using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Reservation.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// M6a (Design §20 "start of M6"). <c>bookings.contact_name</c>: the name given with each booking, backfilled from
    /// the guest's stored name (null stays possible for a walk-in without a name). <c>waitlist_entries.contact_name</c>:
    /// the same for waitlist entries, backfilled the same way, then required. <c>areas.name_zh</c>: the Chinese area name,
    /// set for the two Golden Dragon areas (matching the seed) and copied from the English name for any other area, then
    /// required.
    /// </summary>
    public partial class AddContactNamesAndAreaNamesZh : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "contact_name",
                table: "bookings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "contact_name",
                table: "waitlist_entries",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "name_zh",
                table: "areas",
                type: "text",
                nullable: true);

            migrationBuilder.Sql(
                """
                UPDATE bookings b SET contact_name = g.name FROM guests g WHERE b.guest_id = g.id AND b.contact_name IS NULL;
                UPDATE waitlist_entries w SET contact_name = g.name FROM guests g WHERE w.guest_id = g.id AND w.contact_name IS NULL;
                UPDATE areas SET name_zh = '主厅' WHERE id = '0193a7d0-0000-7000-8000-0000000000a1';
                UPDATE areas SET name_zh = '窗边' WHERE id = '0193a7d0-0000-7000-8000-0000000000a2';
                UPDATE areas SET name_zh = name WHERE name_zh IS NULL;
                """);

            migrationBuilder.AlterColumn<string>(
                name: "contact_name",
                table: "waitlist_entries",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "name_zh",
                table: "areas",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "contact_name",
                table: "waitlist_entries");

            migrationBuilder.DropColumn(
                name: "contact_name",
                table: "bookings");

            migrationBuilder.DropColumn(
                name: "name_zh",
                table: "areas");
        }
    }
}
