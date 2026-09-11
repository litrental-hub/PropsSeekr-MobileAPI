using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PropSeekr.Migrations
{
    /// <inheritdoc />
    public partial class AddVersionedConnectionAttempts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_match_confirmations_match_id_broker_id",
                table: "match_confirmations");

            migrationBuilder.AddColumn<long>(
                name: "connection_request_id",
                table: "reveals",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "listing_version",
                table: "match_connection_requests",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "requirement_version",
                table: "match_connection_requests",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<DateTime>(
                name: "availability_date",
                table: "match_confirmations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "connection_request_id",
                table: "match_confirmations",
                type: "bigint",
                nullable: true);

            // Preserve legacy confirmation/reveal evidence by attaching it to the
            // latest known attempt. If old evidence predates the attempt table,
            // create one terminal synthetic attempt rather than deleting history.
            migrationBuilder.Sql("""
                INSERT INTO match_connection_requests
                    (match_id, requesting_broker_id, receiving_broker_id, listing_version, requirement_version,
                     status, delivery_channel, delivery_status, created_at, responded_at, expires_at)
                SELECT m.matchid,
                       m.listing_broker_id,
                       m.requirement_broker_id,
                       1,
                       1,
                       CASE WHEN rv.match_id IS NOT NULL THEN 'accepted' ELSE 'expired' END,
                       'in_app',
                       'legacy',
                       COALESCE(MIN(mc.created_at), m.created_at, NOW()),
                       NOW(),
                       COALESCE(MAX(mc.window_expires_at), NOW())
                FROM matches m
                LEFT JOIN match_confirmations mc ON mc.match_id = m.matchid
                LEFT JOIN reveals rv ON rv.match_id = m.matchid
                WHERE (mc.match_id IS NOT NULL OR rv.match_id IS NOT NULL)
                  AND NOT EXISTS (SELECT 1 FROM match_connection_requests cr WHERE cr.match_id = m.matchid)
                GROUP BY m.matchid, m.listing_broker_id, m.requirement_broker_id, m.created_at, rv.match_id;

                UPDATE match_confirmations mc
                SET connection_request_id = (
                    SELECT cr.request_id
                    FROM match_connection_requests cr
                    WHERE cr.match_id = mc.match_id
                    ORDER BY cr.request_id DESC
                    LIMIT 1
                );

                UPDATE reveals rv
                SET connection_request_id = (
                    SELECT cr.request_id
                    FROM match_connection_requests cr
                    WHERE cr.match_id = rv.match_id
                    ORDER BY cr.request_id DESC
                    LIMIT 1
                );

                WITH ranked AS (
                    SELECT request_id,
                           row_number() OVER (PARTITION BY match_id ORDER BY request_id DESC) AS position
                    FROM match_connection_requests
                    WHERE status IN ('pending', 'credit_required')
                )
                UPDATE match_connection_requests cr
                SET status = 'expired', responded_at = COALESCE(responded_at, NOW())
                FROM ranked r
                WHERE cr.request_id = r.request_id AND r.position > 1;
                """);

            migrationBuilder.AlterColumn<long>(
                name: "connection_request_id",
                table: "match_confirmations",
                type: "bigint",
                nullable: false,
                oldClrType: typeof(long),
                oldType: "bigint",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_reveals_connection_request_id",
                table: "reveals",
                column: "connection_request_id",
                unique: true,
                filter: "connection_request_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "UX_match_connection_requests_active_match",
                table: "match_connection_requests",
                column: "match_id",
                unique: true,
                filter: "status IN ('pending', 'credit_required')");

            migrationBuilder.CreateIndex(
                name: "IX_match_confirmations_connection_request_id_broker_id",
                table: "match_confirmations",
                columns: new[] { "connection_request_id", "broker_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_match_confirmations_match_id_broker_id",
                table: "match_confirmations",
                columns: new[] { "match_id", "broker_id" });

            migrationBuilder.AddForeignKey(
                name: "FK_match_confirmations_match_connection_requests_connection_re~",
                table: "match_confirmations",
                column: "connection_request_id",
                principalTable: "match_connection_requests",
                principalColumn: "request_id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_match_confirmations_match_connection_requests_connection_re~",
                table: "match_confirmations");

            migrationBuilder.DropIndex(
                name: "IX_reveals_connection_request_id",
                table: "reveals");

            migrationBuilder.DropIndex(
                name: "UX_match_connection_requests_active_match",
                table: "match_connection_requests");

            migrationBuilder.DropIndex(
                name: "IX_match_confirmations_connection_request_id_broker_id",
                table: "match_confirmations");

            migrationBuilder.DropIndex(
                name: "IX_match_confirmations_match_id_broker_id",
                table: "match_confirmations");

            migrationBuilder.DropColumn(
                name: "connection_request_id",
                table: "reveals");

            migrationBuilder.DropColumn(
                name: "listing_version",
                table: "match_connection_requests");

            migrationBuilder.DropColumn(
                name: "requirement_version",
                table: "match_connection_requests");

            migrationBuilder.DropColumn(
                name: "availability_date",
                table: "match_confirmations");

            migrationBuilder.DropColumn(
                name: "connection_request_id",
                table: "match_confirmations");

            migrationBuilder.CreateIndex(
                name: "IX_match_confirmations_match_id_broker_id",
                table: "match_confirmations",
                columns: new[] { "match_id", "broker_id" },
                unique: true);
        }
    }
}
