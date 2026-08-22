using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace PropSeekr.Migrations
{
    /// <inheritdoc />
    public partial class AddListingRequirements : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "Status",
                table: "payments",
                newName: "status");

            migrationBuilder.RenameColumn(
                name: "Gateway",
                table: "payments",
                newName: "gateway");

            migrationBuilder.RenameColumn(
                name: "Currency",
                table: "payments",
                newName: "currency");

            migrationBuilder.RenameColumn(
                name: "Amount",
                table: "payments",
                newName: "amount");

            migrationBuilder.RenameColumn(
                name: "UpdatedAt",
                table: "payments",
                newName: "updated_at");

            migrationBuilder.RenameColumn(
                name: "CreatedAt",
                table: "payments",
                newName: "created_at");
            migrationBuilder.CreateTable(
                name: "listing_requirements",
                columns: table => new
                {
                    listing_requirement_id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    listing_id = table.Column<int>(type: "integer", nullable: false),
                    requirement_id = table.Column<int>(type: "integer", nullable: false),
                    match_status = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    match_score = table.Column<decimal>(type: "numeric", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_listing_requirements", x => x.listing_requirement_id);
                    table.ForeignKey(
                        name: "FK_listing_requirements_listings_listing_id",
                        column: x => x.listing_id,
                        principalTable: "listings_table",
                        principalColumn: "listingid",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_listing_requirements_requirements_requirement_id",
                        column: x => x.requirement_id,
                        principalTable: "requirements_table",
                        principalColumn: "requirementid",
                        onDelete: ReferentialAction.Cascade);
                });
            migrationBuilder.CreateIndex(
                name: "IX_listing_requirements_listing_id_requirement_id",
                table: "listing_requirements",
                columns: new[] { "listing_id", "requirement_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_listing_requirements_requirement_id",
                table: "listing_requirements",
                column: "requirement_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "listing_requirements");
            migrationBuilder.RenameColumn(
                name: "status",
                table: "payments",
                newName: "Status");

            migrationBuilder.RenameColumn(
                name: "gateway",
                table: "payments",
                newName: "Gateway");

            migrationBuilder.RenameColumn(
                name: "currency",
                table: "payments",
                newName: "Currency");

            migrationBuilder.RenameColumn(
                name: "amount",
                table: "payments",
                newName: "Amount");

            migrationBuilder.RenameColumn(
                name: "updated_at",
                table: "payments",
                newName: "UpdatedAt");

            migrationBuilder.RenameColumn(
                name: "created_at",
                table: "payments",
                newName: "CreatedAt");
        }
    }
}
