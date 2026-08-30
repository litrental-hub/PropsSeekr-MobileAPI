-- Read-only deployment verification for the canonical v2 schema.
-- A successful deployment returns no rows from the first query and true for
-- every matching-procedure contract marker in the second query.

WITH expected_indexes(name) AS (
    VALUES
        ('ux_master_city_area_ci'), ('IX_master_city_area'),
        ('ix_master_city_ci'), ('ix_master_area_trgm'),
        ('IX_master_geocoding_status_masterid'),
        ('IX_Users_AadharNumber'), ('IX_Users_PanCard'),
        ('IX_Users_Email_CI'), ('IX_Users_UserName_CI'),
        ('ux_brokers_phone_normalized'),
        ('IX_OtpVerifications_MobileNumber'),
        ('IX_OtpVerifications_MobileNumber_OtpCode'),
        ('IX_EmailOtpRecords_ExpiresAt'),
        ('IX_EmailOtpRecords_Email_Purpose_IsUsed_ExpiresAt'),
        ('IX_PaymentTransactions_RazorpayOrderId'),
        ('IX_PaymentTransactions_Receipt'),
        ('IX_listings_master_id'), ('IX_listings_listing_type'),
        ('IX_listings_property_type'), ('IX_listings_status'),
        ('IX_listings_expires_at'), ('IX_listings_price'),
        ('ix_listings_active_match_scope'),
        ('IX_listings_location_resolution_status_listingid'),
        ('IX_requirements_requirement_type'),
        ('IX_requirements_property_type'), ('IX_requirements_status'),
        ('IX_requirements_expires_at'), ('IX_requirements_budget'),
        ('IX_requirements_configurations'),
        ('IX_requirements_preferred_locality_ids'),
        ('ix_requirements_active_match_scope'),
        ('IX_requirements_location_resolution_status_requirementid'),
        ('IX_location_remediation_jobs_status_available_at'),
        ('IX_matches_status'), ('IX_matches_state'),
        ('ix_matches_requirement_status_score'),
        ('ix_matches_listing_status_score'),
        ('IX_listing_requirements_listing_id_requirement_id'),
        ('IX_match_confirmations_window_expires_at'),
        ('IX_match_connection_requests_requesting_broker_id'),
        ('IX_match_connection_requests_status_expires_at'),
        ('ux_match_connection_requests_one_active'),
        ('ix_notifications_broker_created'),
        ('IX_Notifications_CreatedAt'),
        ('IX_credit_transactions_broker_id_CreatedAt'),
        ('IX_notification_preferences_broker_id'), ('IX_deals_match_id'),
        ('IX_disputes_broker_id_Status')
),
expected_constraints(name) AS (
    VALUES
        ('ck_requirements_radius_km'), ('ck_master_coordinates'),
        ('ck_matches_distinct_brokers'), ('ck_matches_score_range'),
        ('ck_credit_wallets_nonnegative'), ('ck_listing_sizes_positive'),
        ('ck_listing_media_values'), ('ck_listing_details_json'),
        ('CK_Users_Role'),
        ('FK_listings_master_master_id'),
        ('FK_listing_details_listings_listing_id'),
        ('FK_listing_media_listings_listing_id'),
        ('FK_bulk_import_jobs_brokers_broker_id'),
        ('FK_match_connection_requests_matches_match_id'),
        ('FK_match_connection_requests_brokers_requesting_broker_id'),
        ('FK_match_connection_requests_brokers_receiving_broker_id'),
        ('FK_notifications_connection_requests')
),
expected_columns(table_name, column_name) AS (
    VALUES
        ('listings', 'embedding_model'), ('listings', 'isavailable'),
        ('requirements', 'embedding_model'), ('requirements', 'isavailable'),
        ('matches', 'match_tier'), ('matches', 'score_breakdown'),
        ('bulk_import_jobs', 'lock_token')
        ,('bulk_import_jobs', 'default_city')
        ,('master', 'geocoding_status')
        ,('master', 'geocoding_confidence')
        ,('master', 'review_required')
        ,('listings', 'location_resolution_status')
        ,('requirements', 'location_resolution_status')
),
issues AS (
    SELECT 'missing index' AS issue_type, expected.name AS object_name
    FROM expected_indexes expected
    LEFT JOIN pg_indexes actual
      ON actual.schemaname = 'public' AND actual.indexname = expected.name
    WHERE actual.indexname IS NULL
    UNION ALL
    SELECT 'missing constraint', expected.name
    FROM expected_constraints expected
    LEFT JOIN pg_constraint actual
      ON actual.connamespace = 'public'::regnamespace
     AND actual.conname = expected.name
    WHERE actual.oid IS NULL
    UNION ALL
    SELECT 'missing column', expected.table_name || '.' || expected.column_name
    FROM expected_columns expected
    LEFT JOIN information_schema.columns actual
      ON actual.table_schema = 'public'
     AND actual.table_name = expected.table_name
     AND actual.column_name = expected.column_name
    WHERE actual.column_name IS NULL
    UNION ALL
    SELECT 'missing migration', '20260830142328_ReconcileCanonicalDatabaseDesign'
    WHERE NOT EXISTS (
        SELECT 1 FROM public."__EFMigrationsHistory"
        WHERE "MigrationId" = '20260830142328_ReconcileCanonicalDatabaseDesign')
    UNION ALL
    SELECT 'missing migration', '20260830153252_AddTrustedLocationResolution'
    WHERE NOT EXISTS (
        SELECT 1 FROM public."__EFMigrationsHistory"
        WHERE "MigrationId" = '20260830153252_AddTrustedLocationResolution')
)
SELECT * FROM issues ORDER BY issue_type, object_name;

WITH procedure_source AS (
    SELECT LOWER(pg_get_functiondef(
        'public.sp_run_matching_engine(integer,integer)'::regprocedure)) AS body
)
SELECT marker,
       POSITION(fragment IN procedure_source.body) > 0 AS installed
FROM procedure_source
CROSS JOIN (VALUES
    ('progressed matches preserved', 'upper(coalesce(m.status, '''')) = ''matched'''),
    ('same broker excluded', 'l.broker_id <> r.broker_id'),
    ('city hard gate', 'lower(btrim(l.resolved_city)) = lower(btrim(r.resolved_city))'),
    ('trusted geocoding gate', 'locality_geocoding_status in (''resolved'', ''verified'')'),
    ('locality hard gate', 'preferred.locality_similarity >= 0.60'),
    ('fixed budget ceiling', 'l.normalized_price <= r.normalized_budget * 1.10'),
    ('score floor', 'where r.match_score >= 35'),
    ('per requirement cap', 'else requirement_rank <= 50'),
    ('embedding model guard', 'c.requirement_embedding_model is distinct from c.listing_embedding_model')
) contract(marker, fragment)
ORDER BY marker;
