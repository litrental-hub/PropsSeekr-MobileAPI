using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using PropSeekr.Data;

#nullable disable

namespace PropSeekr.Migrations;

/// <summary>
/// Restores the default originally introduced by the dual-handshake migration
/// so stored-procedure match inserts receive their initial state.
/// </summary>
[DbContext(typeof(AppDbContext))]
[Migration("20260830101500_RestoreMatchStateDefault")]
public partial class RestoreMatchStateDefault : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            "ALTER TABLE matches ALTER COLUMN state SET DEFAULT 'matched';");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            "ALTER TABLE matches ALTER COLUMN state DROP DEFAULT;");
    }
}
