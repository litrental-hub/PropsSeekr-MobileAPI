using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PropSeekr.Migrations
{
    /// <inheritdoc />
    public partial class AddPropertyRequest : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
                        migrationBuilder.Sql(@"DO $$
            BEGIN
                -- Rename OTPVerification table to OtpVerifications if needed
                IF EXISTS (SELECT 1 FROM pg_class WHERE relname = 'OTPVerification') THEN
                    IF NOT EXISTS (SELECT 1 FROM pg_class WHERE relname = 'OtpVerifications') THEN
                        ALTER TABLE ""OTPVerification"" RENAME TO ""OtpVerifications"";
                    END IF;
                END IF;

                -- Rename indexes if they exist
                IF EXISTS (SELECT 1 FROM pg_class WHERE relname = 'IX_OTPVerification_MobileNumber_OtpCode') AND NOT EXISTS (SELECT 1 FROM pg_class WHERE relname = 'IX_OtpVerifications_MobileNumber_OtpCode') THEN
                    EXECUTE 'ALTER INDEX ""IX_OTPVerification_MobileNumber_OtpCode"" RENAME TO ""IX_OtpVerifications_MobileNumber_OtpCode""';
                END IF;

                IF EXISTS (SELECT 1 FROM pg_class WHERE relname = 'IX_OTPVerification_MobileNumber') AND NOT EXISTS (SELECT 1 FROM pg_class WHERE relname = 'IX_OtpVerifications_MobileNumber') THEN
                    EXECUTE 'ALTER INDEX ""IX_OTPVerification_MobileNumber"" RENAME TO ""IX_OtpVerifications_MobileNumber""';
                END IF;

                -- Ensure primary key exists on the renamed table
                IF EXISTS (SELECT 1 FROM pg_class WHERE relname = 'OtpVerifications') THEN
                    IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'PK_OtpVerifications') THEN
                        EXECUTE 'ALTER TABLE ""OtpVerifications"" ADD CONSTRAINT ""PK_OtpVerifications"" PRIMARY KEY (""Id"")';
                    END IF;
                END IF;
            END
            $$;");

                        migrationBuilder.Sql(@"CREATE TABLE IF NOT EXISTS ""PropertyRequests"" (
        ""Id"" uuid PRIMARY KEY,
        ""Status"" character varying(50) NOT NULL,
        ""TransactionType"" character varying(50) NOT NULL,
        ""Category"" character varying(50) NOT NULL,
        ""Title"" character varying(500) NOT NULL,
        ""UserId"" uuid NOT NULL,
        ""PreferredLocationsJson"" text NOT NULL DEFAULT '',
        ""BudgetJson"" text NOT NULL DEFAULT '',
        ""RequiredAreaJson"" text NOT NULL DEFAULT '',
        ""UrgencyJson"" text NOT NULL DEFAULT '',
        ""ClientPreferencesJson"" text NOT NULL DEFAULT '',
        ""FiltersJson"" text NOT NULL DEFAULT '',
        ""SearchQueryJson"" text NOT NULL DEFAULT '',
        ""City"" character varying(100) NOT NULL,
        ""Locality"" character varying(100) NOT NULL,
        ""Latitude"" double precision NOT NULL,
        ""Longitude"" double precision NOT NULL,
        ""PostedAt"" timestamp with time zone NOT NULL,
        ""ModifiedDate"" timestamp with time zone NOT NULL
);");

                        migrationBuilder.Sql("CREATE INDEX IF NOT EXISTS \"IX_PropertyRequests_Category\" ON \"PropertyRequests\" (\"Category\");");
                        migrationBuilder.Sql("CREATE INDEX IF NOT EXISTS \"IX_PropertyRequests_City_Locality\" ON \"PropertyRequests\" (\"City\", \"Locality\");");
                        migrationBuilder.Sql("CREATE INDEX IF NOT EXISTS \"IX_PropertyRequests_PostedAt\" ON \"PropertyRequests\" (\"PostedAt\");");
                        migrationBuilder.Sql("CREATE INDEX IF NOT EXISTS \"IX_PropertyRequests_TransactionType\" ON \"PropertyRequests\" (\"TransactionType\");");
                        migrationBuilder.Sql("CREATE INDEX IF NOT EXISTS \"IX_PropertyRequests_UserId\" ON \"PropertyRequests\" (\"UserId\");");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PropertyRequests");

            migrationBuilder.DropPrimaryKey(
                name: "PK_OtpVerifications",
                table: "OtpVerifications");

            migrationBuilder.RenameTable(
                name: "OtpVerifications",
                newName: "OTPVerification");

            migrationBuilder.RenameIndex(
                name: "IX_OtpVerifications_MobileNumber_OtpCode",
                table: "OTPVerification",
                newName: "IX_OTPVerification_MobileNumber_OtpCode");

            migrationBuilder.RenameIndex(
                name: "IX_OtpVerifications_MobileNumber",
                table: "OTPVerification",
                newName: "IX_OTPVerification_MobileNumber");

            migrationBuilder.AddPrimaryKey(
                name: "PK_OTPVerification",
                table: "OTPVerification",
                column: "Id");
        }
    }
}
