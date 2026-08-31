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
