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
            migrationBuilder.Sql(@"CREATE TABLE IF NOT EXISTS ""OTPVerification"" (
    ""Id"" uuid PRIMARY KEY,
    ""MobileNumber"" character varying(10) NOT NULL,
    ""OtpCode"" character varying(6) NOT NULL,
    ""ExpiresAt"" timestamp with time zone NOT NULL,
    ""IsUsed"" boolean NOT NULL,
    ""CreatedDate"" timestamp with time zone NOT NULL
);");

            migrationBuilder.Sql(@"CREATE TABLE IF NOT EXISTS ""Users"" (
    ""Id"" uuid PRIMARY KEY,
    ""Name"" character varying(100) NOT NULL,
    ""MobileNumber"" character varying(10) NOT NULL,
    ""Email"" character varying(255),
    ""AadharNumber"" character varying(12) NOT NULL,
    ""PanCard"" character varying(10) NOT NULL,
    ""GSTNumber"" character varying(20),
    ""ReraRegistrationNumber"" character varying(50),
    ""ProfilePhotoUrl"" text,
    ""IsMobileVerified"" boolean NOT NULL,
    ""CreatedDate"" timestamp with time zone NOT NULL,
    ""ModifiedDate"" timestamp with time zone NOT NULL
);");

            migrationBuilder.Sql("CREATE INDEX IF NOT EXISTS \"IX_OTPVerification_MobileNumber\" ON \"OTPVerification\" (\"MobileNumber\");");

            migrationBuilder.Sql("CREATE INDEX IF NOT EXISTS \"IX_OTPVerification_MobileNumber_OtpCode\" ON \"OTPVerification\" (\"MobileNumber\", \"OtpCode\");");

            migrationBuilder.Sql("CREATE UNIQUE INDEX IF NOT EXISTS \"IX_Users_AadharNumber\" ON \"Users\" (\"AadharNumber\");");

            migrationBuilder.Sql("CREATE UNIQUE INDEX IF NOT EXISTS \"IX_Users_MobileNumber\" ON \"Users\" (\"MobileNumber\");");

            migrationBuilder.Sql("CREATE UNIQUE INDEX IF NOT EXISTS \"IX_Users_PanCard\" ON \"Users\" (\"PanCard\");");
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
