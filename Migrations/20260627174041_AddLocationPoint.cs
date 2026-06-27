using Microsoft.EntityFrameworkCore.Migrations;
using NetTopologySuite.Geometries;

#nullable disable

namespace PropSeekr.Migrations
{
    /// <inheritdoc />
    public partial class AddLocationPoint : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:postgis", ",,");

            migrationBuilder.AddColumn<Point>(
                name: "Location",
                table: "PropertyRequests",
                type: "geography (point)",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_PropertyRequests_Location",
                table: "PropertyRequests",
                column: "Location")
                .Annotation("Npgsql:IndexMethod", "GIST");

            // Backfill Location from existing Latitude/Longitude values
            migrationBuilder.Sql(
                @"UPDATE ""PropertyRequests""
                  SET ""Location"" = ST_SetSRID(ST_MakePoint(""Longitude"", ""Latitude""), 4326)::geography
                  WHERE ""Latitude"" != 0 OR ""Longitude"" != 0;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PropertyRequests_Location",
                table: "PropertyRequests");

            migrationBuilder.DropColumn(
                name: "Location",
                table: "PropertyRequests");

            migrationBuilder.AlterDatabase()
                .OldAnnotation("Npgsql:PostgresExtension:postgis", ",,");
        }
    }
}
