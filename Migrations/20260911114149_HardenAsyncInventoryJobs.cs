using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PropSeekr.Migrations
{
    /// <inheritdoc />
    public partial class HardenAsyncInventoryJobs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "content_version",
                table: "requirements",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<string>(
                name: "embedding_status",
                table: "requirements",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "queued");

            migrationBuilder.AddColumn<int>(
                name: "embedding_version",
                table: "requirements",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "content_version",
                table: "listings",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<string>(
                name: "embedding_status",
                table: "listings",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "queued");

            migrationBuilder.AddColumn<int>(
                name: "embedding_version",
                table: "listings",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "heartbeat_at",
                table: "embedding_jobs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "lock_token",
                table: "embedding_jobs",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "target_version",
                table: "embedding_jobs",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<string>(
                name: "upload_etag",
                table: "bulk_import_jobs",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "upload_size_bytes",
                table: "bulk_import_jobs",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "upload_verified_at",
                table: "bulk_import_jobs",
                type: "timestamp with time zone",
                nullable: true);

            // Reconcile existing vectors before enforcing the versioned state machine.
            // Only active, available records with trusted locations are backfilled.
            migrationBuilder.Sql("""
                UPDATE listings
                SET embedding_version = CASE WHEN embedding IS NOT NULL THEN 1 ELSE NULL END,
                    embedding_status = CASE
                        WHEN embedding IS NOT NULL THEN 'completed'
                        WHEN isavailable = TRUE
                             AND lower(coalesce(status, 'active')) = 'active'
                             AND (expires_at IS NULL OR expires_at > NOW())
                             AND location_resolution_status IN ('verified', 'resolved') THEN 'queued'
                        ELSE 'not_required'
                    END;

                UPDATE requirements
                SET embedding_version = CASE WHEN embedding IS NOT NULL THEN 1 ELSE NULL END,
                    embedding_status = CASE
                        WHEN embedding IS NOT NULL THEN 'completed'
                        WHEN isavailable = TRUE
                             AND lower(coalesce(status, 'active')) = 'active'
                             AND (expires_at IS NULL OR expires_at > NOW())
                             AND location_resolution_status IN ('verified', 'resolved') THEN 'queued'
                        ELSE 'not_required'
                    END;

                INSERT INTO embedding_jobs
                    (id, entity_type, entity_id, target_version, status, attempt_count, max_attempts,
                     available_at, created_at, updated_at)
                SELECT gen_random_uuid(), 'listing', listingid, content_version, 'queued', 0, 5,
                       NOW(), NOW(), NOW()
                FROM listings l
                WHERE l.embedding_status = 'queued'
                  AND NOT EXISTS (
                      SELECT 1 FROM embedding_jobs j
                      WHERE j.entity_type = 'listing' AND j.entity_id = l.listingid AND j.status = 'queued');

                INSERT INTO embedding_jobs
                    (id, entity_type, entity_id, target_version, status, attempt_count, max_attempts,
                     available_at, created_at, updated_at)
                SELECT gen_random_uuid(), 'requirement', requirementid, content_version, 'queued', 0, 5,
                       NOW(), NOW(), NOW()
                FROM requirements r
                WHERE r.embedding_status = 'queued'
                  AND NOT EXISTS (
                      SELECT 1 FROM embedding_jobs j
                      WHERE j.entity_type = 'requirement' AND j.entity_id = r.requirementid AND j.status = 'queued');
                """);

            migrationBuilder.AddCheckConstraint(
                name: "CK_requirements_content_version_positive",
                table: "requirements",
                sql: "content_version > 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_requirements_embedding_status_valid",
                table: "requirements",
                sql: "embedding_status IN ('queued', 'processing', 'completed', 'failed', 'not_required')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_requirements_embedding_version_valid",
                table: "requirements",
                sql: "embedding_version IS NULL OR (embedding_version > 0 AND embedding_version <= content_version)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_listings_content_version_positive",
                table: "listings",
                sql: "content_version > 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_listings_embedding_status_valid",
                table: "listings",
                sql: "embedding_status IN ('queued', 'processing', 'completed', 'failed', 'not_required')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_listings_embedding_version_valid",
                table: "listings",
                sql: "embedding_version IS NULL OR (embedding_version > 0 AND embedding_version <= content_version)");

            migrationBuilder.CreateIndex(
                name: "IX_embedding_jobs_status_heartbeat_at",
                table: "embedding_jobs",
                columns: new[] { "status", "heartbeat_at" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_embedding_jobs_status_valid",
                table: "embedding_jobs",
                sql: "status IN ('queued', 'processing', 'completed', 'failed', 'superseded')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_embedding_jobs_target_version_positive",
                table: "embedding_jobs",
                sql: "target_version > 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_bulk_import_jobs_upload_size_positive",
                table: "bulk_import_jobs",
                sql: "upload_size_bytes IS NULL OR upload_size_bytes > 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_requirements_content_version_positive",
                table: "requirements");

            migrationBuilder.DropCheckConstraint(
                name: "CK_requirements_embedding_status_valid",
                table: "requirements");

            migrationBuilder.DropCheckConstraint(
                name: "CK_requirements_embedding_version_valid",
                table: "requirements");

            migrationBuilder.DropCheckConstraint(
                name: "CK_listings_content_version_positive",
                table: "listings");

            migrationBuilder.DropCheckConstraint(
                name: "CK_listings_embedding_status_valid",
                table: "listings");

            migrationBuilder.DropCheckConstraint(
                name: "CK_listings_embedding_version_valid",
                table: "listings");

            migrationBuilder.DropIndex(
                name: "IX_embedding_jobs_status_heartbeat_at",
                table: "embedding_jobs");

            migrationBuilder.DropCheckConstraint(
                name: "CK_embedding_jobs_status_valid",
                table: "embedding_jobs");

            migrationBuilder.DropCheckConstraint(
                name: "CK_embedding_jobs_target_version_positive",
                table: "embedding_jobs");

            migrationBuilder.DropCheckConstraint(
                name: "CK_bulk_import_jobs_upload_size_positive",
                table: "bulk_import_jobs");

            migrationBuilder.DropColumn(
                name: "content_version",
                table: "requirements");

            migrationBuilder.DropColumn(
                name: "embedding_status",
                table: "requirements");

            migrationBuilder.DropColumn(
                name: "embedding_version",
                table: "requirements");

            migrationBuilder.DropColumn(
                name: "content_version",
                table: "listings");

            migrationBuilder.DropColumn(
                name: "embedding_status",
                table: "listings");

            migrationBuilder.DropColumn(
                name: "embedding_version",
                table: "listings");

            migrationBuilder.DropColumn(
                name: "heartbeat_at",
                table: "embedding_jobs");

            migrationBuilder.DropColumn(
                name: "lock_token",
                table: "embedding_jobs");

            migrationBuilder.DropColumn(
                name: "target_version",
                table: "embedding_jobs");

            migrationBuilder.DropColumn(
                name: "upload_etag",
                table: "bulk_import_jobs");

            migrationBuilder.DropColumn(
                name: "upload_size_bytes",
                table: "bulk_import_jobs");

            migrationBuilder.DropColumn(
                name: "upload_verified_at",
                table: "bulk_import_jobs");
        }
    }
}
