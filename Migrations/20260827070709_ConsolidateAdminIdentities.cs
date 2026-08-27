using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PropSeekr.Migrations
{
    /// <inheritdoc />
    public partial class ConsolidateAdminIdentities : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(name: "UserName", table: "Users", type: "character varying(100)", maxLength: 100, nullable: true);
            migrationBuilder.AddColumn<bool>(name: "IsActive", table: "Users", type: "boolean", nullable: false, defaultValue: true);
            migrationBuilder.AlterColumn<string>(name: "MobileNumber", table: "Users", type: "character varying(10)", maxLength: 10, nullable: true, oldClrType: typeof(string), oldType: "character varying(10)", oldMaxLength: 10);
            migrationBuilder.AlterColumn<string>(name: "AadharNumber", table: "Users", type: "character varying(12)", maxLength: 12, nullable: true, oldClrType: typeof(string), oldType: "character varying(12)", oldMaxLength: 12);
            migrationBuilder.AlterColumn<string>(name: "PanCard", table: "Users", type: "character varying(10)", maxLength: 10, nullable: true, oldClrType: typeof(string), oldType: "character varying(10)", oldMaxLength: 10);

            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM "AdminUsers" a JOIN "Users" u ON u."Id" = a."Id") THEN
                        RAISE EXCEPTION 'Cannot consolidate identities: an AdminUsers Id already exists in Users.';
                    END IF;
                    IF EXISTS (SELECT 1 FROM "AdminUsers" a JOIN "AdminUsers" b ON lower(a."UserName") = lower(b."UserName") AND a."Id" <> b."Id") THEN
                        RAISE EXCEPTION 'Cannot consolidate identities: AdminUsers contains duplicate usernames.';
                    END IF;
                END $$;

                INSERT INTO "Users" ("Id", "Name", "UserName", "PasswordHash", "IsActive", "Role", "IsMobileVerified", "IsEmailVerified", "Credits", "CreatedDate", "ModifiedDate")
                SELECT a."Id", a."UserName", lower(a."UserName"), a."PasswordHash", a."IsActive", 'Admin', true, true, 0, a."CreatedDate", a."ModifiedDate"
                FROM "AdminUsers" a;

                CREATE UNIQUE INDEX "IX_Users_UserName_CI" ON "Users" (lower("UserName")) WHERE "UserName" IS NOT NULL;
                """);

            migrationBuilder.DropTable(name: "AdminUsers");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new global::System.NotSupportedException("This data-consolidation migration is intentionally non-reversible.");
        }
    }
}
