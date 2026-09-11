using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PropSeekr.Migrations
{
    /// <inheritdoc />
    public partial class AddWidgetOtpChallenges : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "widget_otp_challenges",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MobileNumber = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: true),
                    PendingRegistrationId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ConsumedTokenHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_widget_otp_challenges", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_widget_otp_challenges_ConsumedTokenHash",
                table: "widget_otp_challenges",
                column: "ConsumedTokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_widget_otp_challenges_MobileNumber_CreatedAt",
                table: "widget_otp_challenges",
                columns: new[] { "MobileNumber", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "widget_otp_challenges");
        }
    }
}
