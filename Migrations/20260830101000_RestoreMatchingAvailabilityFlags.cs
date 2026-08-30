using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using PropSeekr.Data;

#nullable disable

namespace PropSeekr.Migrations;

/// <summary>
/// Restores the availability flags consumed by the existing matching
/// procedure. No scoring, filtering, or threshold logic is changed.
/// </summary>
[DbContext(typeof(AppDbContext))]
[Migration("20260830101000_RestoreMatchingAvailabilityFlags")]
public partial class RestoreMatchingAvailabilityFlags : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            ALTER TABLE listings
                ADD COLUMN IF NOT EXISTS isavailable boolean NOT NULL DEFAULT TRUE;

            ALTER TABLE requirements
                ADD COLUMN IF NOT EXISTS isavailable boolean NOT NULL DEFAULT TRUE;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            ALTER TABLE requirements DROP COLUMN IF EXISTS isavailable;
            ALTER TABLE listings DROP COLUMN IF EXISTS isavailable;
            """);
    }
}
