using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PropSeekr.Migrations;

[Migration("20260827000100_PersistedIdentityRoles")]
public partial class PersistedIdentityRoles : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "Role",
            table: "Users",
            type: "character varying(30)",
            maxLength: 30,
            nullable: false,
            defaultValue: "User");

        migrationBuilder.AddColumn<string>(
            name: "Role",
            table: "AdminUsers",
            type: "character varying(30)",
            maxLength: 30,
            nullable: false,
            defaultValue: "Admin");

        migrationBuilder.Sql("""
            ALTER TABLE "Users"
            ADD CONSTRAINT "CK_Users_Role" CHECK ("Role" IN ('User', 'Admin'));

            ALTER TABLE "AdminUsers"
            ADD CONSTRAINT "CK_AdminUsers_Role" CHECK ("Role" IN ('User', 'Admin'));
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropCheckConstraint(name: "CK_Users_Role", table: "Users");
        migrationBuilder.DropCheckConstraint(name: "CK_AdminUsers_Role", table: "AdminUsers");
        migrationBuilder.DropColumn(name: "Role", table: "Users");
        migrationBuilder.DropColumn(name: "Role", table: "AdminUsers");
    }
}
