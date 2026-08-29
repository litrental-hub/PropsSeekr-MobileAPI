using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PropSeekr.Migrations
{
    /// <inheritdoc />
    public partial class AddBulkImportJobs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "bulk_import_jobs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    broker_id = table.Column<int>(type: "integer", nullable: false),
                    storage_key = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    original_file_name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    attempt_count = table.Column<int>(type: "integer", nullable: false),
                    max_attempts = table.Column<int>(type: "integer", nullable: false),
                    available_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    locked_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    completed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    listings_inserted = table.Column<int>(type: "integer", nullable: false),
                    requirements_inserted = table.Column<int>(type: "integer", nullable: false),
                    skipped_records = table.Column<int>(type: "integer", nullable: false),
                    failed_records = table.Column<int>(type: "integer", nullable: false),
                    last_error = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_bulk_import_jobs", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_bulk_import_jobs_broker_id",
                table: "bulk_import_jobs",
                column: "broker_id");

            migrationBuilder.CreateIndex(
                name: "IX_bulk_import_jobs_status_available_at",
                table: "bulk_import_jobs",
                columns: new[] { "status", "available_at" });

            migrationBuilder.CreateIndex(
                name: "IX_bulk_import_jobs_storage_key",
                table: "bulk_import_jobs",
                column: "storage_key",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "bulk_import_jobs");
        }
    }
}
