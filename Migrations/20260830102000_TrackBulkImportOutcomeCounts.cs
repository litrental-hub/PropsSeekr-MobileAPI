using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using PropSeekr.Data;

#nullable disable

namespace PropSeekr.Migrations;

/// <summary>
/// Preserves skipped and failed counts in the ingestion receipt so a retry
/// that resumes at embedding reports the original ingestion outcome.
/// </summary>
[DbContext(typeof(AppDbContext))]
[Migration("20260830102000_TrackBulkImportOutcomeCounts")]
public partial class TrackBulkImportOutcomeCounts : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            ALTER TABLE processed_files
                ADD COLUMN IF NOT EXISTS skipped_records integer NOT NULL DEFAULT 0;

            ALTER TABLE processed_files
                ADD COLUMN IF NOT EXISTS failed_records integer NOT NULL DEFAULT 0;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            ALTER TABLE processed_files DROP COLUMN IF EXISTS failed_records;
            ALTER TABLE processed_files DROP COLUMN IF EXISTS skipped_records;
            """);
    }
}
