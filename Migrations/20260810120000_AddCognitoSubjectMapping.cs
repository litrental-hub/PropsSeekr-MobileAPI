using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PropSeekr.Migrations;

public partial class AddCognitoSubjectMapping : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "CognitoSubject",
            table: "Users",
            type: "character varying(255)",
            maxLength: 255,
            nullable: true);

        migrationBuilder.CreateIndex(
            name: "IX_Users_CognitoSubject",
            table: "Users",
            column: "CognitoSubject",
            unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_Users_CognitoSubject",
            table: "Users");

        migrationBuilder.DropColumn(
            name: "CognitoSubject",
            table: "Users");
    }
}
