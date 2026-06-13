using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PropSeekr.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "OTPVerification",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MobileNumber = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    OtpCode = table.Column<string>(type: "character varying(6)", maxLength: 6, nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsUsed = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OTPVerification", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Users",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    MobileNumber = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Email = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    AadharNumber = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    PanCard = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    GSTNumber = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    ReraRegistrationNumber = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    ProfilePhotoUrl = table.Column<string>(type: "text", nullable: true),
                    IsMobileVerified = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ModifiedDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Users", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OTPVerification_MobileNumber",
                table: "OTPVerification",
                column: "MobileNumber");

            migrationBuilder.CreateIndex(
                name: "IX_OTPVerification_MobileNumber_OtpCode",
                table: "OTPVerification",
                columns: new[] { "MobileNumber", "OtpCode" });

            migrationBuilder.CreateIndex(
                name: "IX_Users_AadharNumber",
                table: "Users",
                column: "AadharNumber",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Users_MobileNumber",
                table: "Users",
                column: "MobileNumber",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Users_PanCard",
                table: "Users",
                column: "PanCard",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OTPVerification");

            migrationBuilder.DropTable(
                name: "Users");
        }
    }
}
