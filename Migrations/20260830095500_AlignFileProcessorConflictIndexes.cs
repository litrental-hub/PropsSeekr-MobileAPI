using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using PropSeekr.Data;

#nullable disable

namespace PropSeekr.Migrations;

/// <summary>
/// Makes the partial-index predicates identical to the predicates used by the
/// file processor's ON CONFLICT clauses so PostgreSQL can infer the indexes.
/// </summary>
[DbContext(typeof(AppDbContext))]
[Migration("20260830095500_AlignFileProcessorConflictIndexes")]
public partial class AlignFileProcessorConflictIndexes : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            DROP INDEX IF EXISTS ux_listings_active_content_hash;
            CREATE UNIQUE INDEX ux_listings_active_content_hash
                ON listings (content_hash)
                WHERE status = 'ACTIVE';

            DROP INDEX IF EXISTS ux_requirements_active_content_hash;
            CREATE UNIQUE INDEX ux_requirements_active_content_hash
                ON requirements (content_hash)
                WHERE status = 'ACTIVE';
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            DROP INDEX IF EXISTS ux_requirements_active_content_hash;
            CREATE UNIQUE INDEX ux_requirements_active_content_hash
                ON requirements (content_hash)
                WHERE status = 'ACTIVE' AND content_hash IS NOT NULL;

            DROP INDEX IF EXISTS ux_listings_active_content_hash;
            CREATE UNIQUE INDEX ux_listings_active_content_hash
                ON listings (content_hash)
                WHERE status = 'ACTIVE' AND content_hash IS NOT NULL;
            """);
    }
}
