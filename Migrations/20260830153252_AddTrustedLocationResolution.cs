using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PropSeekr.Migrations
{
    /// <inheritdoc />
    public partial class AddTrustedLocationResolution : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "location_resolution_note",
                table: "requirements",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "location_resolution_status",
                table: "requirements",
                type: "character varying(24)",
                maxLength: 24,
                nullable: false,
                defaultValue: "missing");

            migrationBuilder.AddColumn<DateTime>(
                name: "location_resolved_at",
                table: "requirements",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "formatted_address",
                table: "master",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "geocoded_at",
                table: "master",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "geocoding_confidence",
                table: "master",
                type: "numeric",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "geocoding_error",
                table: "master",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "geocoding_provider",
                table: "master",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "geocoding_status",
                table: "master",
                type: "character varying(24)",
                maxLength: 24,
                nullable: false,
                defaultValue: "pending");

            migrationBuilder.AddColumn<string>(
                name: "location_precision",
                table: "master",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "provider_place_id",
                table: "master",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "review_required",
                table: "master",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "location_resolution_note",
                table: "listings",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "location_resolution_status",
                table: "listings",
                type: "character varying(24)",
                maxLength: 24,
                nullable: false,
                defaultValue: "missing");

            migrationBuilder.AddColumn<DateTime>(
                name: "location_resolved_at",
                table: "listings",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "default_city",
                table: "bulk_import_jobs",
                type: "character varying(100)",
                maxLength: 100,
                nullable: false,
                defaultValue: "Indore");

            migrationBuilder.Sql("""
                UPDATE public.master
                SET geocoding_status = CASE
                        WHEN lat IS NOT NULL AND lng IS NOT NULL THEN 'resolved'
                        ELSE 'pending'
                    END,
                    geocoding_provider = CASE
                        WHEN lat IS NOT NULL AND lng IS NOT NULL THEN 'legacy'
                        ELSE NULL
                    END,
                    geocoding_confidence = CASE
                        WHEN lat IS NOT NULL AND lng IS NOT NULL THEN 0.80
                        ELSE NULL
                    END,
                    geocoded_at = CASE
                        WHEN lat IS NOT NULL AND lng IS NOT NULL THEN NOW()
                        ELSE NULL
                    END,
                    review_required = FALSE;

                UPDATE public.listings l
                SET location_resolution_status = CASE
                        WHEN m.masterid IS NOT NULL AND m.lat IS NOT NULL AND m.lng IS NOT NULL THEN 'resolved'
                        WHEN l.master_id IS NOT NULL THEN 'review_required'
                        ELSE 'missing'
                    END,
                    location_resolution_note = CASE
                        WHEN m.masterid IS NOT NULL AND m.lat IS NOT NULL AND m.lng IS NOT NULL
                            THEN 'Migrated from an existing canonical locality.'
                        WHEN l.master_id IS NOT NULL
                            THEN 'Canonical locality is missing trusted coordinates.'
                        ELSE NULL
                    END,
                    location_resolved_at = CASE
                        WHEN m.masterid IS NOT NULL AND m.lat IS NOT NULL AND m.lng IS NOT NULL THEN NOW()
                        ELSE NULL
                    END
                FROM public.master m
                WHERE m.masterid = l.master_id;

                UPDATE public.requirements r
                SET location_resolution_status = CASE
                        WHEN EXISTS (
                            SELECT 1 FROM public.master m
                            WHERE m.masterid = ANY(r.preferred_locality_ids)
                              AND m.lat IS NOT NULL AND m.lng IS NOT NULL)
                            THEN 'resolved'
                        WHEN r.preferred_locality_ids IS NOT NULL
                             AND array_length(r.preferred_locality_ids, 1) > 0
                            THEN 'review_required'
                        ELSE 'missing'
                    END,
                    location_resolution_note = CASE
                        WHEN EXISTS (
                            SELECT 1 FROM public.master m
                            WHERE m.masterid = ANY(r.preferred_locality_ids)
                              AND m.lat IS NOT NULL AND m.lng IS NOT NULL)
                            THEN 'Migrated from an existing canonical locality.'
                        WHEN r.preferred_locality_ids IS NOT NULL
                             AND array_length(r.preferred_locality_ids, 1) > 0
                            THEN 'Canonical locality is missing trusted coordinates.'
                        ELSE NULL
                    END,
                    location_resolved_at = CASE
                        WHEN EXISTS (
                            SELECT 1 FROM public.master m
                            WHERE m.masterid = ANY(r.preferred_locality_ids)
                              AND m.lat IS NOT NULL AND m.lng IS NOT NULL)
                            THEN NOW()
                        ELSE NULL
                    END;
                """);

            migrationBuilder.CreateTable(
                name: "location_remediation_jobs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    requested_by_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    default_city = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false, defaultValue: "Indore"),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, defaultValue: "queued"),
                    stage = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false, defaultValue: "master"),
                    cursor_id = table.Column<int>(type: "integer", nullable: false),
                    batch_size = table.Column<int>(type: "integer", nullable: false, defaultValue: 25),
                    master_resolved = table.Column<int>(type: "integer", nullable: false),
                    listings_resolved = table.Column<int>(type: "integer", nullable: false),
                    requirements_resolved = table.Column<int>(type: "integer", nullable: false),
                    review_required = table.Column<int>(type: "integer", nullable: false),
                    lock_token = table.Column<Guid>(type: "uuid", nullable: true),
                    locked_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    available_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    last_error = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_location_remediation_jobs", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_requirements_location_resolution_status_requirementid",
                table: "requirements",
                columns: new[] { "location_resolution_status", "requirementid" });

            migrationBuilder.CreateIndex(
                name: "IX_master_geocoding_status_masterid",
                table: "master",
                columns: new[] { "geocoding_status", "masterid" });

            migrationBuilder.CreateIndex(
                name: "IX_listings_location_resolution_status_listingid",
                table: "listings",
                columns: new[] { "location_resolution_status", "listingid" });

            migrationBuilder.CreateIndex(
                name: "IX_location_remediation_jobs_status_available_at",
                table: "location_remediation_jobs",
                columns: new[] { "status", "available_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "location_remediation_jobs");

            migrationBuilder.DropIndex(
                name: "IX_requirements_location_resolution_status_requirementid",
                table: "requirements");

            migrationBuilder.DropIndex(
                name: "IX_master_geocoding_status_masterid",
                table: "master");

            migrationBuilder.DropIndex(
                name: "IX_listings_location_resolution_status_listingid",
                table: "listings");

            migrationBuilder.DropColumn(
                name: "location_resolution_note",
                table: "requirements");

            migrationBuilder.DropColumn(
                name: "location_resolution_status",
                table: "requirements");

            migrationBuilder.DropColumn(
                name: "location_resolved_at",
                table: "requirements");

            migrationBuilder.DropColumn(
                name: "formatted_address",
                table: "master");

            migrationBuilder.DropColumn(
                name: "geocoded_at",
                table: "master");

            migrationBuilder.DropColumn(
                name: "geocoding_confidence",
                table: "master");

            migrationBuilder.DropColumn(
                name: "geocoding_error",
                table: "master");

            migrationBuilder.DropColumn(
                name: "geocoding_provider",
                table: "master");

            migrationBuilder.DropColumn(
                name: "geocoding_status",
                table: "master");

            migrationBuilder.DropColumn(
                name: "location_precision",
                table: "master");

            migrationBuilder.DropColumn(
                name: "provider_place_id",
                table: "master");

            migrationBuilder.DropColumn(
                name: "review_required",
                table: "master");

            migrationBuilder.DropColumn(
                name: "location_resolution_note",
                table: "listings");

            migrationBuilder.DropColumn(
                name: "location_resolution_status",
                table: "listings");

            migrationBuilder.DropColumn(
                name: "location_resolved_at",
                table: "listings");

            migrationBuilder.DropColumn(
                name: "default_city",
                table: "bulk_import_jobs");
        }
    }
}
