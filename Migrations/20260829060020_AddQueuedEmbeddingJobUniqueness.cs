using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PropSeekr.Migrations
{
    /// <inheritdoc />
    public partial class AddQueuedEmbeddingJobUniqueness : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_embedding_jobs_entity_type_entity_id",
                table: "embedding_jobs");

            migrationBuilder.CreateIndex(
                name: "UX_embedding_jobs_one_queued_per_entity",
                table: "embedding_jobs",
                columns: new[] { "entity_type", "entity_id" },
                unique: true,
                filter: "status = 'queued'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_embedding_jobs_one_queued_per_entity",
                table: "embedding_jobs");

            migrationBuilder.CreateIndex(
                name: "IX_embedding_jobs_entity_type_entity_id",
                table: "embedding_jobs",
                columns: new[] { "entity_type", "entity_id" });
        }
    }
}
