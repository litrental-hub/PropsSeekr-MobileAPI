using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using PropSeekr.Data;

#nullable disable

namespace PropSeekr.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20260824000300_SeedPurchasableCreditPacks")]
public partial class SeedPurchasableCreditPacks : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            INSERT INTO credit_packs ("Name", "Credits", "Price", "Active", "CreatedAt")
            SELECT seed.name, seed.credits, seed.price, TRUE, NOW()
            FROM (VALUES
                ('Starter Pack', 10, 3000.00::numeric),
                ('Growth Pack', 20, 5600.00::numeric),
                ('Pro Pack', 50, 12500.00::numeric)
            ) AS seed(name, credits, price)
            WHERE NOT EXISTS (
                SELECT 1
                FROM credit_packs existing
                WHERE existing."Credits" = seed.credits
                  AND existing."Price" = seed.price
            );

            UPDATE credit_packs
            SET "Active" = TRUE
            WHERE ("Credits", "Price") IN (
                (10, 3000.00::numeric),
                (20, 5600.00::numeric),
                (50, 12500.00::numeric)
            );
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DELETE FROM credit_packs
            WHERE ("Name", "Credits", "Price") IN (
                ('Starter Pack', 10, 3000.00::numeric),
                ('Growth Pack', 20, 5600.00::numeric),
                ('Pro Pack', 50, 12500.00::numeric)
            );
            """);
    }
}
