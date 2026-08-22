using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PropSeekr.Migrations
{
    /// <inheritdoc />
    public partial class LinkUserAndBroker : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "BrokerId",
                table: "Users",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Users_BrokerId",
                table: "Users",
                column: "BrokerId");

            migrationBuilder.AddForeignKey(
                name: "FK_Users_brokers_BrokerId",
                table: "Users",
                column: "BrokerId",
                principalTable: "brokers",
                principalColumn: "brokerid",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Users_brokers_BrokerId",
                table: "Users");

            migrationBuilder.DropIndex(
                name: "IX_Users_BrokerId",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "BrokerId",
                table: "Users");
        }
    }
}
