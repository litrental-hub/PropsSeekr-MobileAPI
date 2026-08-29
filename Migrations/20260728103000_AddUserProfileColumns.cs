using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PropSeekr.Migrations
{
    public partial class AddUserProfileColumns : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Earlier development snapshots already created this column. Keep
            // the migration safe for both historical and clean databases.
            migrationBuilder.Sql("ALTER TABLE \"Users\" ADD COLUMN IF NOT EXISTS \"IsEmailVerified\" boolean NOT NULL DEFAULT FALSE;");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("ALTER TABLE \"Users\" DROP COLUMN IF EXISTS \"IsEmailVerified\";");
        }
    }
}
