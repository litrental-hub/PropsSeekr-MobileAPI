using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace PropSeekr.Migrations;

/// <summary>
/// Reconciles databases whose migration history was copied independently of
/// their physical canonical schema. The migration is intentionally idempotent:
/// listings/requirements remain tables and legacy *_table objects are untouched.
/// </summary>
public partial class ReconcileCanonicalDatabaseDesign : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            ALTER TABLE public.listings
                ADD COLUMN IF NOT EXISTS embedding_model text,
                ADD COLUMN IF NOT EXISTS isavailable boolean NOT NULL DEFAULT true;
            ALTER TABLE public.requirements
                ADD COLUMN IF NOT EXISTS embedding_model text,
                ADD COLUMN IF NOT EXISTS isavailable boolean NOT NULL DEFAULT true;
            ALTER TABLE public.matches
                ADD COLUMN IF NOT EXISTS match_tier character varying(16),
                ADD COLUMN IF NOT EXISTS score_breakdown jsonb;
            ALTER TABLE public.bulk_import_jobs
                ADD COLUMN IF NOT EXISTS lock_token uuid;

            UPDATE public.brokers SET response_score = 100.00 WHERE response_score IS NULL;
            UPDATE public.brokers SET status = 'ACTIVE' WHERE NULLIF(BTRIM(status), '') IS NULL;
            UPDATE public.brokers SET created_at = NOW() WHERE created_at IS NULL;
            ALTER TABLE public.brokers ALTER COLUMN response_score SET DEFAULT 100.00;
            ALTER TABLE public.brokers ALTER COLUMN status SET DEFAULT 'ACTIVE';
            ALTER TABLE public.brokers ALTER COLUMN created_at SET DEFAULT NOW();

            CREATE TEMP TABLE propseekr_master_merge ON COMMIT DROP AS
            SELECT masterid AS duplicate_id, keeper_id
            FROM (
                SELECT
                    masterid,
                    MIN(masterid) OVER (
                        PARTITION BY LOWER(BTRIM(city)), LOWER(BTRIM(area))) AS keeper_id,
                    COUNT(*) OVER (
                        PARTITION BY LOWER(BTRIM(city)), LOWER(BTRIM(area))) AS duplicate_count
                FROM public.master
                WHERE NULLIF(BTRIM(city), '') IS NOT NULL
                  AND NULLIF(BTRIM(area), '') IS NOT NULL
            ) ranked
            WHERE duplicate_count > 1 AND masterid <> keeper_id;

            UPDATE public.listings listing
            SET master_id = merge.keeper_id
            FROM propseekr_master_merge merge
            WHERE listing.master_id = merge.duplicate_id;

            UPDATE public.requirements requirement
            SET preferred_locality_ids = (
                SELECT ARRAY(
                    SELECT mapped_id
                    FROM (
                        SELECT
                            COALESCE(merge.keeper_id, locality.master_id) AS mapped_id,
                            MIN(locality.ordinality) AS first_position
                        FROM UNNEST(requirement.preferred_locality_ids)
                            WITH ORDINALITY AS locality(master_id, ordinality)
                        LEFT JOIN propseekr_master_merge merge
                            ON merge.duplicate_id = locality.master_id
                        GROUP BY COALESCE(merge.keeper_id, locality.master_id)
                    ) normalized
                    ORDER BY first_position
                )
            )
            WHERE EXISTS (
                SELECT 1
                FROM UNNEST(requirement.preferred_locality_ids) AS locality(locality_id)
                JOIN propseekr_master_merge merge
                    ON merge.duplicate_id = locality.locality_id);

            DELETE FROM public.master master_row
            USING propseekr_master_merge merge
            WHERE master_row.masterid = merge.duplicate_id;

            CREATE UNIQUE INDEX IF NOT EXISTS ux_master_city_area_ci
                ON public.master (LOWER(BTRIM(city)), LOWER(BTRIM(area)))
                WHERE NULLIF(BTRIM(city), '') IS NOT NULL
                  AND NULLIF(BTRIM(area), '') IS NOT NULL;
            CREATE INDEX IF NOT EXISTS "IX_master_city_area" ON public.master (city, area);
            CREATE INDEX IF NOT EXISTS ix_master_city_ci ON public.master (LOWER(BTRIM(city)));
            CREATE INDEX IF NOT EXISTS ix_master_area_trgm
                ON public.master USING gin (LOWER(area) gin_trgm_ops);

            CREATE UNIQUE INDEX IF NOT EXISTS "IX_Users_AadharNumber"
                ON public."Users" ("AadharNumber") WHERE "AadharNumber" IS NOT NULL;
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_Users_PanCard"
                ON public."Users" ("PanCard") WHERE "PanCard" IS NOT NULL;
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_Users_Email_CI"
                ON public."Users" (LOWER("Email")) WHERE "Email" IS NOT NULL;
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_Users_UserName_CI"
                ON public."Users" (LOWER("UserName")) WHERE "UserName" IS NOT NULL;
            CREATE UNIQUE INDEX IF NOT EXISTS ux_brokers_phone_normalized
                ON public.brokers (RIGHT(REGEXP_REPLACE(phone_number, '\D', '', 'g'), 10));

            CREATE INDEX IF NOT EXISTS "IX_OtpVerifications_MobileNumber"
                ON public."OtpVerifications" ("MobileNumber");
            CREATE INDEX IF NOT EXISTS "IX_OtpVerifications_MobileNumber_OtpCode"
                ON public."OtpVerifications" ("MobileNumber", "OtpCode");
            CREATE INDEX IF NOT EXISTS "IX_EmailOtpRecords_ExpiresAt"
                ON public."EmailOtpRecords" ("ExpiresAt");
            CREATE INDEX IF NOT EXISTS "IX_EmailOtpRecords_Email_Purpose_IsUsed_ExpiresAt"
                ON public."EmailOtpRecords" ("Email", "Purpose", "IsUsed", "ExpiresAt");
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_PaymentTransactions_RazorpayOrderId"
                ON public."PaymentTransactions" ("RazorpayOrderId");
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_PaymentTransactions_Receipt"
                ON public."PaymentTransactions" ("Receipt");

            CREATE INDEX IF NOT EXISTS "IX_listings_master_id" ON public.listings (master_id);
            CREATE INDEX IF NOT EXISTS "IX_listings_listing_type" ON public.listings (listing_type);
            CREATE INDEX IF NOT EXISTS "IX_listings_property_type" ON public.listings (property_type);
            CREATE INDEX IF NOT EXISTS "IX_listings_status" ON public.listings (status);
            CREATE INDEX IF NOT EXISTS "IX_listings_expires_at" ON public.listings (expires_at);
            CREATE INDEX IF NOT EXISTS "IX_listings_price" ON public.listings (price);
            CREATE INDEX IF NOT EXISTS ix_listings_active_match_scope
                ON public.listings (UPPER(listing_type), UPPER(property_type), master_id, broker_id)
                WHERE UPPER(COALESCE(status, '')) = 'ACTIVE' AND isavailable;

            CREATE INDEX IF NOT EXISTS "IX_requirements_requirement_type"
                ON public.requirements (requirement_type);
            CREATE INDEX IF NOT EXISTS "IX_requirements_property_type"
                ON public.requirements (property_type);
            CREATE INDEX IF NOT EXISTS "IX_requirements_status" ON public.requirements (status);
            CREATE INDEX IF NOT EXISTS "IX_requirements_expires_at" ON public.requirements (expires_at);
            CREATE INDEX IF NOT EXISTS "IX_requirements_budget" ON public.requirements (budget);
            CREATE INDEX IF NOT EXISTS "IX_requirements_configurations"
                ON public.requirements USING gin (configurations);
            CREATE INDEX IF NOT EXISTS "IX_requirements_preferred_locality_ids"
                ON public.requirements USING gin (preferred_locality_ids);
            CREATE INDEX IF NOT EXISTS ix_requirements_active_match_scope
                ON public.requirements (UPPER(requirement_type), UPPER(property_type), broker_id)
                WHERE UPPER(COALESCE(status, '')) = 'ACTIVE' AND isavailable;

            CREATE INDEX IF NOT EXISTS "IX_matches_status" ON public.matches (status);
            CREATE INDEX IF NOT EXISTS "IX_matches_state" ON public.matches (state);
            CREATE INDEX IF NOT EXISTS ix_matches_requirement_status_score
                ON public.matches (requirement_id, status, match_score DESC);
            CREATE INDEX IF NOT EXISTS ix_matches_listing_status_score
                ON public.matches (listing_id, status, match_score DESC);

            DROP INDEX IF EXISTS public."IX_listing_requirements_listing_id";
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_listing_requirements_listing_id_requirement_id"
                ON public.listing_requirements (listing_id, requirement_id);

            CREATE INDEX IF NOT EXISTS "IX_match_confirmations_window_expires_at"
                ON public.match_confirmations (window_expires_at);
            CREATE INDEX IF NOT EXISTS "IX_match_connection_requests_requesting_broker_id"
                ON public.match_connection_requests (requesting_broker_id);
            CREATE INDEX IF NOT EXISTS "IX_match_connection_requests_status_expires_at"
                ON public.match_connection_requests (status, expires_at);
            CREATE UNIQUE INDEX IF NOT EXISTS ux_match_connection_requests_one_active
                ON public.match_connection_requests (match_id)
                WHERE status IN ('pending', 'credit_required');
            CREATE INDEX IF NOT EXISTS ix_notifications_broker_created
                ON public.notifications (broker_id, created_at DESC);
            CREATE INDEX IF NOT EXISTS "IX_Notifications_CreatedAt"
                ON public."Notifications" ("CreatedAt" DESC);
            CREATE INDEX IF NOT EXISTS "IX_credit_transactions_broker_id_CreatedAt"
                ON public.credit_transactions (broker_id, "CreatedAt" DESC);

            DROP INDEX IF EXISTS public."IX_notification_preferences_broker_id";
            CREATE UNIQUE INDEX "IX_notification_preferences_broker_id"
                ON public.notification_preferences (broker_id);
            DROP INDEX IF EXISTS public."IX_deals_match_id";
            CREATE UNIQUE INDEX "IX_deals_match_id" ON public.deals (match_id);
            DROP INDEX IF EXISTS public."IX_disputes_broker_id";
            CREATE INDEX IF NOT EXISTS "IX_disputes_broker_id_Status"
                ON public.disputes (broker_id, "Status");

            CREATE INDEX IF NOT EXISTS "IX_PropertyRequests_BudgetMax"
                ON public."PropertyRequests" ("BudgetMax");
            CREATE INDEX IF NOT EXISTS "IX_PropertyRequests_BudgetMin"
                ON public."PropertyRequests" ("BudgetMin");
            CREATE INDEX IF NOT EXISTS "IX_PropertyRequests_Category"
                ON public."PropertyRequests" ("Category");
            CREATE INDEX IF NOT EXISTS "IX_PropertyRequests_City_Locality"
                ON public."PropertyRequests" ("City", "Locality");
            CREATE INDEX IF NOT EXISTS "IX_PropertyRequests_PostedAt"
                ON public."PropertyRequests" ("PostedAt");
            CREATE INDEX IF NOT EXISTS "IX_PropertyRequests_PropertyTypesJson"
                ON public."PropertyRequests" ("PropertyTypesJson");
            CREATE INDEX IF NOT EXISTS "IX_PropertyRequests_TransactionType"
                ON public."PropertyRequests" ("TransactionType");

            ALTER TABLE public.requirements DROP CONSTRAINT IF EXISTS ck_requirements_radius_km;
            ALTER TABLE public.requirements ADD CONSTRAINT ck_requirements_radius_km
                CHECK (radius_km IS NULL OR (radius_km > 0 AND radius_km <= 100));
            ALTER TABLE public.master DROP CONSTRAINT IF EXISTS ck_master_coordinates;
            ALTER TABLE public.master ADD CONSTRAINT ck_master_coordinates CHECK (
                (lat IS NULL AND lng IS NULL)
                OR (lat BETWEEN -90 AND 90 AND lng BETWEEN -180 AND 180));
            ALTER TABLE public.matches DROP CONSTRAINT IF EXISTS ck_matches_distinct_brokers;
            ALTER TABLE public.matches ADD CONSTRAINT ck_matches_distinct_brokers
                CHECK (listing_broker_id <> requirement_broker_id);
            ALTER TABLE public.matches DROP CONSTRAINT IF EXISTS ck_matches_score_range;
            ALTER TABLE public.matches ADD CONSTRAINT ck_matches_score_range
                CHECK (match_score IS NULL OR match_score BETWEEN 0 AND 100);
            ALTER TABLE public.credit_wallets DROP CONSTRAINT IF EXISTS ck_credit_wallets_nonnegative;
            ALTER TABLE public.credit_wallets ADD CONSTRAINT ck_credit_wallets_nonnegative
                CHECK (free_credits_balance >= 0 AND paid_credits_balance >= 0);
            ALTER TABLE public.listing_sizes DROP CONSTRAINT IF EXISTS ck_listing_sizes_positive;
            ALTER TABLE public.listing_sizes ADD CONSTRAINT ck_listing_sizes_positive CHECK (size_sqft > 0);
            ALTER TABLE public.listing_media DROP CONSTRAINT IF EXISTS ck_listing_media_values;
            ALTER TABLE public.listing_media ADD CONSTRAINT ck_listing_media_values CHECK (
                media_type IN ('image', 'video') AND file_size_bytes > 0 AND sort_order >= 0);
            ALTER TABLE public.listing_details DROP CONSTRAINT IF EXISTS ck_listing_details_json;
            ALTER TABLE public.listing_details ADD CONSTRAINT ck_listing_details_json CHECK (
                JSONB_TYPEOF(details_json) = 'object'
                AND OCTET_LENGTH(details_json::text) <= 32768);
            ALTER TABLE public."Users" DROP CONSTRAINT IF EXISTS "CK_Users_Role";
            ALTER TABLE public."Users" ADD CONSTRAINT "CK_Users_Role"
                CHECK ("Role" IN ('User', 'Admin'));

            DO $$
            BEGIN
                IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'FK_listings_master_master_id') THEN
                    ALTER TABLE public.listings ADD CONSTRAINT "FK_listings_master_master_id"
                        FOREIGN KEY (master_id) REFERENCES public.master(masterid) ON DELETE RESTRICT;
                END IF;
                IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'FK_listing_details_listings_listing_id') THEN
                    ALTER TABLE public.listing_details ADD CONSTRAINT "FK_listing_details_listings_listing_id"
                        FOREIGN KEY (listing_id) REFERENCES public.listings(listingid) ON DELETE CASCADE;
                END IF;
                IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'FK_listing_media_listings_listing_id') THEN
                    ALTER TABLE public.listing_media ADD CONSTRAINT "FK_listing_media_listings_listing_id"
                        FOREIGN KEY (listing_id) REFERENCES public.listings(listingid) ON DELETE CASCADE;
                END IF;
                IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'FK_bulk_import_jobs_brokers_broker_id') THEN
                    ALTER TABLE public.bulk_import_jobs ADD CONSTRAINT "FK_bulk_import_jobs_brokers_broker_id"
                        FOREIGN KEY (broker_id) REFERENCES public.brokers(brokerid) ON DELETE RESTRICT;
                END IF;
                IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'FK_match_connection_requests_matches_match_id') THEN
                    ALTER TABLE public.match_connection_requests ADD CONSTRAINT "FK_match_connection_requests_matches_match_id"
                        FOREIGN KEY (match_id) REFERENCES public.matches(matchid) ON DELETE CASCADE;
                END IF;
                IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'FK_match_connection_requests_brokers_requesting_broker_id') THEN
                    ALTER TABLE public.match_connection_requests ADD CONSTRAINT "FK_match_connection_requests_brokers_requesting_broker_id"
                        FOREIGN KEY (requesting_broker_id) REFERENCES public.brokers(brokerid) ON DELETE RESTRICT;
                END IF;
                IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'FK_match_connection_requests_brokers_receiving_broker_id') THEN
                    ALTER TABLE public.match_connection_requests ADD CONSTRAINT "FK_match_connection_requests_brokers_receiving_broker_id"
                        FOREIGN KEY (receiving_broker_id) REFERENCES public.brokers(brokerid) ON DELETE RESTRICT;
                END IF;
                IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'FK_notifications_connection_requests') THEN
                    ALTER TABLE public.notifications ADD CONSTRAINT "FK_notifications_connection_requests"
                        FOREIGN KEY (connection_request_id)
                        REFERENCES public.match_connection_requests(request_id) ON DELETE SET NULL;
                END IF;
            END $$;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            ALTER TABLE public.notifications DROP CONSTRAINT IF EXISTS "FK_notifications_connection_requests";
            ALTER TABLE public.match_connection_requests DROP CONSTRAINT IF EXISTS "FK_match_connection_requests_brokers_receiving_broker_id";
            ALTER TABLE public.match_connection_requests DROP CONSTRAINT IF EXISTS "FK_match_connection_requests_brokers_requesting_broker_id";
            ALTER TABLE public.match_connection_requests DROP CONSTRAINT IF EXISTS "FK_match_connection_requests_matches_match_id";
            ALTER TABLE public.bulk_import_jobs DROP CONSTRAINT IF EXISTS "FK_bulk_import_jobs_brokers_broker_id";
            ALTER TABLE public.listing_media DROP CONSTRAINT IF EXISTS "FK_listing_media_listings_listing_id";
            ALTER TABLE public.listing_details DROP CONSTRAINT IF EXISTS "FK_listing_details_listings_listing_id";
            ALTER TABLE public.listings DROP CONSTRAINT IF EXISTS "FK_listings_master_master_id";
            ALTER TABLE public.requirements DROP CONSTRAINT IF EXISTS ck_requirements_radius_km;
            ALTER TABLE public.master DROP CONSTRAINT IF EXISTS ck_master_coordinates;
            ALTER TABLE public.matches DROP CONSTRAINT IF EXISTS ck_matches_distinct_brokers;
            ALTER TABLE public.matches DROP CONSTRAINT IF EXISTS ck_matches_score_range;
            ALTER TABLE public.credit_wallets DROP CONSTRAINT IF EXISTS ck_credit_wallets_nonnegative;
            ALTER TABLE public.listing_sizes DROP CONSTRAINT IF EXISTS ck_listing_sizes_positive;
            ALTER TABLE public.listing_media DROP CONSTRAINT IF EXISTS ck_listing_media_values;
            ALTER TABLE public.listing_details DROP CONSTRAINT IF EXISTS ck_listing_details_json;
            DROP INDEX IF EXISTS public.ux_match_connection_requests_one_active;
            DROP INDEX IF EXISTS public.ix_matches_requirement_status_score;
            DROP INDEX IF EXISTS public.ix_matches_listing_status_score;
            DROP INDEX IF EXISTS public.ix_requirements_active_match_scope;
            DROP INDEX IF EXISTS public.ix_listings_active_match_scope;
            DROP INDEX IF EXISTS public.ix_notifications_broker_created;
            DROP INDEX IF EXISTS public.ix_master_area_trgm;
            DROP INDEX IF EXISTS public.ix_master_city_ci;
            DROP INDEX IF EXISTS public.ux_master_city_area_ci;
            """);
    }
}
