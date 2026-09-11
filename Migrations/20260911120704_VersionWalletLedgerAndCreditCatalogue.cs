using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PropSeekr.Migrations
{
    /// <inheritdoc />
    public partial class VersionWalletLedgerAndCreditCatalogue : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "free_balance_after",
                table: "credit_transactions",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "free_credits_amount",
                table: "credit_transactions",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "paid_balance_after",
                table: "credit_transactions",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "paid_credits_amount",
                table: "credit_transactions",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "period_key",
                table: "credit_transactions",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "AmountInPaise",
                table: "credit_packs",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<string>(
                name: "Code",
                table: "credit_packs",
                type: "character varying(50)",
                maxLength: 50,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "Currency",
                table: "credit_packs",
                type: "character varying(10)",
                maxLength: 10,
                nullable: false,
                defaultValue: "INR");

            migrationBuilder.AddColumn<DateTime>(
                name: "EffectiveFrom",
                table: "credit_packs",
                type: "timestamp with time zone",
                nullable: false,
                defaultValueSql: "NOW()");

            migrationBuilder.AddColumn<DateTime>(
                name: "EffectiveTo",
                table: "credit_packs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Version",
                table: "credit_packs",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.Sql("""
                UPDATE credit_packs
                SET "Code" = 'CREDITS_' || "Credits"::text,
                    "Currency" = 'INR',
                    "AmountInPaise" = round("Price" * 100)::bigint,
                    "EffectiveFrom" = COALESCE("CreatedAt", NOW());

                WITH versions AS (
                    SELECT "Id",
                           row_number() OVER (PARTITION BY "Code" ORDER BY "CreatedAt", "Id") AS version
                    FROM credit_packs
                )
                UPDATE credit_packs cp
                SET "Version" = versions.version
                FROM versions
                WHERE cp."Id" = versions."Id";
                """);

            migrationBuilder.AddCheckConstraint(
                name: "CK_credit_wallets_free_nonnegative",
                table: "credit_wallets",
                sql: "free_credits_balance >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_credit_wallets_paid_nonnegative",
                table: "credit_wallets",
                sql: "paid_credits_balance >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_credit_transactions_allocation_valid",
                table: "credit_transactions",
                sql: "(free_credits_amount IS NULL AND paid_credits_amount IS NULL) OR (free_credits_amount >= 0 AND paid_credits_amount >= 0 AND free_credits_amount + paid_credits_amount = \"Amount\")");

            migrationBuilder.AddCheckConstraint(
                name: "CK_credit_transactions_amount_positive",
                table: "credit_transactions",
                sql: "\"Amount\" > 0");

            migrationBuilder.CreateIndex(
                name: "IX_credit_packs_Code_Version",
                table: "credit_packs",
                columns: new[] { "Code", "Version" },
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_credit_packs_amount_positive",
                table: "credit_packs",
                sql: "\"AmountInPaise\" > 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_credit_packs_credits_positive",
                table: "credit_packs",
                sql: "\"Credits\" > 0");

            migrationBuilder.AddForeignKey(
                name: "FK_reveals_match_connection_requests_connection_request_id",
                table: "reveals",
                column: "connection_request_id",
                principalTable: "match_connection_requests",
                principalColumn: "request_id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_reveals_match_connection_requests_connection_request_id",
                table: "reveals");

            migrationBuilder.DropCheckConstraint(
                name: "CK_credit_wallets_free_nonnegative",
                table: "credit_wallets");

            migrationBuilder.DropCheckConstraint(
                name: "CK_credit_wallets_paid_nonnegative",
                table: "credit_wallets");

            migrationBuilder.DropCheckConstraint(
                name: "CK_credit_transactions_allocation_valid",
                table: "credit_transactions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_credit_transactions_amount_positive",
                table: "credit_transactions");

            migrationBuilder.DropIndex(
                name: "IX_credit_packs_Code_Version",
                table: "credit_packs");

            migrationBuilder.DropCheckConstraint(
                name: "CK_credit_packs_amount_positive",
                table: "credit_packs");

            migrationBuilder.DropCheckConstraint(
                name: "CK_credit_packs_credits_positive",
                table: "credit_packs");

            migrationBuilder.DropColumn(
                name: "free_balance_after",
                table: "credit_transactions");

            migrationBuilder.DropColumn(
                name: "free_credits_amount",
                table: "credit_transactions");

            migrationBuilder.DropColumn(
                name: "paid_balance_after",
                table: "credit_transactions");

            migrationBuilder.DropColumn(
                name: "paid_credits_amount",
                table: "credit_transactions");

            migrationBuilder.DropColumn(
                name: "period_key",
                table: "credit_transactions");

            migrationBuilder.DropColumn(
                name: "AmountInPaise",
                table: "credit_packs");

            migrationBuilder.DropColumn(
                name: "Code",
                table: "credit_packs");

            migrationBuilder.DropColumn(
                name: "Currency",
                table: "credit_packs");

            migrationBuilder.DropColumn(
                name: "EffectiveFrom",
                table: "credit_packs");

            migrationBuilder.DropColumn(
                name: "EffectiveTo",
                table: "credit_packs");

            migrationBuilder.DropColumn(
                name: "Version",
                table: "credit_packs");
        }
    }
}
