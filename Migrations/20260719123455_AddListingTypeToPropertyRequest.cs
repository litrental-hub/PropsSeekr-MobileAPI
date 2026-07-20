using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PropSeekr.Migrations
{
    /// <inheritdoc />
    public partial class AddListingTypeToPropertyRequest : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ListingType",
                table: "PropertyRequests",
                type: "character varying(50)",
                maxLength: 50,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "IX_PropertyRequests_ListingType",
                table: "PropertyRequests",
                column: "ListingType");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PropertyRequests_ListingType",
                table: "PropertyRequests");

            migrationBuilder.DropColumn(
                name: "ListingType",
                table: "PropertyRequests");
        }
    }
}
