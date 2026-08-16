using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PropSeekr.Migrations;

public partial class AddAppAttestation : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "AppAttestationChallenges",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                UserId = table.Column<Guid>(type: "uuid", nullable: false),
                Platform = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                Purpose = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                Nonce = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                UsedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
            }, constraints: table => table.PrimaryKey("PK_AppAttestationChallenges", x => x.Id));

        migrationBuilder.CreateTable(
            name: "TrustedAppInstances",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                UserId = table.Column<Guid>(type: "uuid", nullable: false),
                Platform = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                KeyId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                PublicKeySpkiBase64 = table.Column<string>(type: "text", nullable: true),
                Status = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                AppVersion = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                Environment = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                AssertionCounter = table.Column<long>(type: "bigint", nullable: false),
                CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                LastSeenAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                RevokedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                IsRevoked = table.Column<bool>(type: "boolean", nullable: false)
            }, constraints: table => table.PrimaryKey("PK_TrustedAppInstances", x => x.Id));

        migrationBuilder.CreateIndex(name: "IX_AppAttestationChallenges_Nonce", table: "AppAttestationChallenges", column: "Nonce", unique: true);
        migrationBuilder.CreateIndex(name: "IX_AppAttestationChallenges_UserId_Purpose_ExpiresAt", table: "AppAttestationChallenges", columns: new[] { "UserId", "Purpose", "ExpiresAt" });
        migrationBuilder.CreateIndex(name: "IX_TrustedAppInstances_Platform_KeyId", table: "TrustedAppInstances", columns: new[] { "Platform", "KeyId" }, unique: true);
        migrationBuilder.CreateIndex(name: "IX_TrustedAppInstances_UserId", table: "TrustedAppInstances", column: "UserId");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "AppAttestationChallenges");
        migrationBuilder.DropTable(name: "TrustedAppInstances");
    }
}
