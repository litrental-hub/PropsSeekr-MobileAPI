using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PropSeekr.Migrations
{
    /// <inheritdoc />
    public partial class AddPropertyRequestFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "BudgetMax",
                table: "PropertyRequests",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "BudgetMin",
                table: "PropertyRequests",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PropertyTypesJson",
                table: "PropertyRequests",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "IX_PropertyRequests_BudgetMax",
                table: "PropertyRequests",
                column: "BudgetMax");

            migrationBuilder.CreateIndex(
                name: "IX_PropertyRequests_BudgetMin",
                table: "PropertyRequests",
                column: "BudgetMin");

            migrationBuilder.CreateIndex(
                name: "IX_PropertyRequests_PropertyTypesJson",
                table: "PropertyRequests",
                column: "PropertyTypesJson");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PropertyRequests_BudgetMax",
                table: "PropertyRequests");

            migrationBuilder.DropIndex(
                name: "IX_PropertyRequests_BudgetMin",
                table: "PropertyRequests");

            migrationBuilder.DropIndex(
                name: "IX_PropertyRequests_PropertyTypesJson",
                table: "PropertyRequests");

            migrationBuilder.DropColumn(
                name: "BudgetMax",
                table: "PropertyRequests");

            migrationBuilder.DropColumn(
                name: "BudgetMin",
                table: "PropertyRequests");

            migrationBuilder.DropColumn(
                name: "PropertyTypesJson",
                table: "PropertyRequests");
        }
    }
}
