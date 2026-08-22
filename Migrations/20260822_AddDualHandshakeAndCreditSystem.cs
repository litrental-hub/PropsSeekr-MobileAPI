using Microsoft.EntityFrameworkCore.Migrations;
using System;

#nullable disable

namespace PropSeekr.Migrations
{
    /// <inheritdoc />
    public partial class AddDualHandshakeAndCreditSystem : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // No-op: these tables already exist in snake_case schema in DB.
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // No-op: do not drop pre-existing tables.
        }
    }
}
