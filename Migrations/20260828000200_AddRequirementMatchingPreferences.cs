using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using PropSeekr.Data;

#nullable disable

namespace PropSeekr.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20260828000200_AddRequirementMatchingPreferences")]
public partial class AddRequirementMatchingPreferences : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            ALTER TABLE public.requirements_table
                ADD COLUMN IF NOT EXISTS city character varying(100),
                ADD COLUMN IF NOT EXISTS posted_by character varying(50),
                ADD COLUMN IF NOT EXISTS embedding_model text,
                ADD COLUMN IF NOT EXISTS budget_min numeric,
                ADD COLUMN IF NOT EXISTS size_max numeric,
                ADD COLUMN IF NOT EXISTS radius_km double precision,
                ADD COLUMN IF NOT EXISTS preferred_project_names text[];

            ALTER TABLE public.requirements_table
                DROP CONSTRAINT IF EXISTS ck_requirements_radius_km;
            ALTER TABLE public.requirements_table
                ADD CONSTRAINT ck_requirements_radius_km
                CHECK (radius_km IS NULL OR (radius_km > 0 AND radius_km <= 100));

            CREATE OR REPLACE VIEW public.requirements AS
            SELECT
                requirementid,
                broker_id,
                source,
                raw_message_text,
                requirement_type,
                property_type,
                configurations,
                preferred_locality_ids,
                budget,
                budget_unit,
                size,
                furnishing_pref,
                facing_pref,
                status,
                expires_at,
                search_vector,
                embedding,
                created_at,
                updated_at,
                content_hash,
                group_name,
                message_datetime,
                budget_type,
                isavailable,
                last_confirmed_at,
                freshness_score,
                freshness_category,
                freshness_updated_at,
                city,
                posted_by,
                embedding_model,
                budget_min,
                size_max,
                radius_km,
                preferred_project_names
            FROM public.requirements_table
            WHERE isavailable = true;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            DROP VIEW IF EXISTS public.requirements;
            CREATE OR REPLACE VIEW public.requirements AS
            SELECT
                requirementid,
                broker_id,
                source,
                raw_message_text,
                requirement_type,
                property_type,
                configurations,
                preferred_locality_ids,
                budget,
                budget_unit,
                size,
                furnishing_pref,
                facing_pref,
                status,
                expires_at,
                search_vector,
                embedding,
                created_at,
                updated_at,
                content_hash,
                group_name,
                message_datetime,
                budget_type,
                isavailable,
                last_confirmed_at,
                freshness_score,
                freshness_category,
                freshness_updated_at,
                city,
                posted_by,
                embedding_model
            FROM public.requirements_table
            WHERE isavailable = true;

            ALTER TABLE public.requirements_table
                DROP CONSTRAINT IF EXISTS ck_requirements_radius_km,
                DROP COLUMN IF EXISTS preferred_project_names,
                DROP COLUMN IF EXISTS radius_km,
                DROP COLUMN IF EXISTS size_max,
                DROP COLUMN IF EXISTS budget_min;
            """);
    }
}
