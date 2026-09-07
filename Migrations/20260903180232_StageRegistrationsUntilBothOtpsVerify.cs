using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PropSeekr.Migrations
{
    /// <inheritdoc />
    public partial class StageRegistrationsUntilBothOtpsVerify : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "pending_registrations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    mobile_number = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    email = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    password_hash = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    address_line1 = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    address_line2 = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    city = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    state = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    pincode = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    aadhar_number = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    pan_card = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    gst_number = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    rera_registration_number = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_pending_registrations", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_pending_registrations_aadhar_number",
                table: "pending_registrations",
                column: "aadhar_number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_pending_registrations_email",
                table: "pending_registrations",
                column: "email",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_pending_registrations_expires_at",
                table: "pending_registrations",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "IX_pending_registrations_mobile_number",
                table: "pending_registrations",
                column: "mobile_number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_pending_registrations_pan_card",
                table: "pending_registrations",
                column: "pan_card",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "pending_registrations");
        }
    }
}
