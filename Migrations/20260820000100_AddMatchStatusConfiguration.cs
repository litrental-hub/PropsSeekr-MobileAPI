using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace PropSeekr.Migrations
{
    public partial class AddMatchStatusConfiguration : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1. Create match_statuses table
            migrationBuilder.CreateTable(
                name: "match_statuses",
                columns: table => new
                {
                    status_id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    status_name = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    color_code = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    message = table.Column<string>(type: "text", nullable: false),
                    display_order = table.Column<int>(type: "integer", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_match_statuses", x => x.status_id);
                });

            // 2. Seed the 4 default statuses
            migrationBuilder.InsertData(
                table: "match_statuses",
                columns: new[] { "status_name", "color_code", "message", "display_order", "is_active" },
                values: new object[,]
                {
                    { "Not Interested", "#FF0000", "Not Interested", 1, true },
                    { "Agreed / Match Confirmed", "#0000FF", "Agreed / Match Confirmed", 2, true },
                    { "Discussion / Talking Phase", "#FFA500", "Discussion / Talking Phase", 3, true },
                    { "Issue / Bug Detected", "#FFFF00", "Issue / Bug Detected", 4, true }
                });

            // 3. Add status_id column to matches table
            migrationBuilder.AddColumn<int>(
                name: "status_id",
                table: "matches",
                type: "integer",
                nullable: true);

            // 4. Create foreign key from matches(status_id) to match_statuses(status_id)
            migrationBuilder.AddForeignKey(
                name: "FK_matches_match_statuses_status_id",
                table: "matches",
                column: "status_id",
                principalTable: "match_statuses",
                principalColumn: "status_id",
                onDelete: ReferentialAction.Restrict);

            // 5. Update existing matches: set status_id to the 'Not Interested' status (status_id = 1)
            migrationBuilder.Sql("UPDATE matches SET status_id = 1 WHERE status_id IS NULL;");

            // 6. Create index on matches(status_id)
            migrationBuilder.CreateIndex(
                name: "IX_matches_status_id",
                table: "matches",
                column: "status_id");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_matches_match_statuses_status_id",
                table: "matches");

            migrationBuilder.DropIndex(
                name: "IX_matches_status_id",
                table: "matches");

            migrationBuilder.DropColumn(
                name: "status_id",
                table: "matches");

            migrationBuilder.DropTable(
                name: "match_statuses");
        }
    }
}
