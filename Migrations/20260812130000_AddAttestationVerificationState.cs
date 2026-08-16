using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PropSeekr.Migrations;

public partial class AddAttestationVerificationState : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<DateTime>(name: "VerifiedAt", table: "AppAttestationChallenges", type: "timestamp with time zone", nullable: true);
        migrationBuilder.AddColumn<string>(name: "VerifiedPlatform", table: "AppAttestationChallenges", type: "character varying(20)", maxLength: 20, nullable: true);
        migrationBuilder.AddColumn<string>(name: "VerifiedRequestHash", table: "AppAttestationChallenges", type: "character varying(64)", maxLength: 64, nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "VerifiedAt", table: "AppAttestationChallenges");
        migrationBuilder.DropColumn(name: "VerifiedPlatform", table: "AppAttestationChallenges");
        migrationBuilder.DropColumn(name: "VerifiedRequestHash", table: "AppAttestationChallenges");
    }
}
