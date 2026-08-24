using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using PropSeekr.Data;

#nullable disable

namespace PropSeekr.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20260824000200_AddCreditTransactionReferenceKey")]
public partial class AddCreditTransactionReferenceKey : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "reference_key",
            table: "credit_transactions",
            type: "character varying(100)",
            maxLength: 100,
            nullable: true);

        migrationBuilder.CreateIndex(
            name: "IX_credit_transactions_broker_id_reference_type_reference_key",
            table: "credit_transactions",
            columns: new[] { "broker_id", "reference_type", "reference_key" },
            unique: true,
            filter: "reference_key IS NOT NULL");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_credit_transactions_broker_id_reference_type_reference_key",
            table: "credit_transactions");

        migrationBuilder.DropColumn(
            name: "reference_key",
            table: "credit_transactions");
    }
}
