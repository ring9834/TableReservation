using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Reservation.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Data only (M4b): <c>VenueSettings.ContactPhone</c> is a new optional key in the <c>settings</c> jsonb. Rows without
    /// it read as <c>null</c>; this fills in the fictional Golden Dragon number so an existing local database matches a
    /// fresh seed. Other venues are left alone. <c>settings_version</c> is not bumped: no booking terms change.
    /// </summary>
    public partial class AddVenueContactPhone : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                UPDATE venues
                SET settings = jsonb_set(settings, '{contactPhone}', to_jsonb('+64 9 000 0000'::text))
                WHERE id = '0193a7d0-0000-7000-8000-000000000001'
                  AND (NOT settings ? 'contactPhone' OR settings -> 'contactPhone' = 'null'::jsonb);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                UPDATE venues
                SET settings = settings - 'contactPhone'
                WHERE id = '0193a7d0-0000-7000-8000-000000000001';
                """);
        }
    }
}
