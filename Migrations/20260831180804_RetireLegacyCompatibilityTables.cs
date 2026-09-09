using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Infrastructure;
using PropSeekr.Data;

#nullable disable

namespace PropSeekr.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20260831180804_RetireLegacyCompatibilityTables")]
public partial class RetireLegacyCompatibilityTables : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // A DEV audit does not establish that these tables are empty in every
        // deployment. Fail before the first drop if any history needs archiving.
        // Locks remain held by the migration transaction through the drops.
        migrationBuilder.Sql("""
            DO $guard$
            DECLARE
                legacy_table text;
                has_rows boolean;
            BEGIN
                FOREACH legacy_table IN ARRAY ARRAY[
                    'deals', 'disputes', 'match_statuses', 'payments',
                    'UnlockedProperties', 'visits', 'PropertyRequests', 'Notifications']
                LOOP
                    EXECUTE format('LOCK TABLE %I IN ACCESS EXCLUSIVE MODE', legacy_table);
                    EXECUTE format('SELECT EXISTS (SELECT 1 FROM %I LIMIT 1)', legacy_table) INTO has_rows;
                    IF has_rows THEN
                        RAISE EXCEPTION 'Refusing to retire non-empty legacy table %. Archive and verify its data before retrying this migration.', legacy_table;
                    END IF;
                END LOOP;
            END
            $guard$;
            """);
        // PostgreSQL treats quoted "Notifications" and unquoted notifications as
        // distinct relations. The lowercase broker notification stream is canonical.
        migrationBuilder.DropTable(name: "deals");
        migrationBuilder.DropTable(name: "disputes");
        migrationBuilder.DropTable(name: "match_statuses");
        migrationBuilder.DropTable(name: "payments");
        migrationBuilder.DropTable(name: "UnlockedProperties");
        migrationBuilder.DropTable(name: "visits");
        migrationBuilder.DropTable(name: "PropertyRequests");
        migrationBuilder.DropTable(name: "Notifications");
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Retired compatibility tables are intentionally not recreated by rollback.");
}
