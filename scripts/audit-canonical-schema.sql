-- Read-only canonical schema/data-integrity audit for the currently connected DB.
-- Run with ON_ERROR_STOP and verify current_database() before interpreting output.

SELECT current_database() AS database_name, current_user AS database_user;

WITH required_relations(name, kind) AS (
    VALUES
        ('Users', 'r'), ('OtpVerifications', 'r'), ('EmailOtpRecords', 'r'),
        ('PropertyRequests', 'r'), ('PaymentTransactions', 'r'),
        ('UnlockedProperties', 'r'), ('Notifications', 'r'),
        ('brokers', 'r'), ('master', 'r'),
        ('listings', 'r'), ('requirements', 'r'), ('matches', 'r'),
        ('listing_sizes', 'r'), ('listing_details', 'r'), ('listing_media', 'r'),
        ('listing_requirements', 'r'), ('match_statuses', 'r'),
        ('match_connection_requests', 'r'), ('match_confirmations', 'r'),
        ('reveals', 'r'), ('credit_wallets', 'r'), ('credit_transactions', 'r'),
        ('credit_packs', 'r'), ('payments', 'r'),
        ('notifications', 'r'), ('notification_preferences', 'r'),
        ('deals', 'r'), ('visits', 'r'), ('disputes', 'r'),
        ('embedding_jobs', 'r'), ('bulk_import_jobs', 'r'), ('processed_files', 'r')
)
SELECT required.name, required.kind AS expected_kind,
       COALESCE(actual.relkind::text, 'missing') AS actual_kind
FROM required_relations required
LEFT JOIN pg_class actual
    ON actual.relname = required.name
   AND actual.relnamespace = 'public'::regnamespace
WHERE actual.oid IS NULL OR actual.relkind::text <> required.kind
ORDER BY required.name;

WITH checks AS (
    SELECT 'listing.master orphan' check_name, COUNT(*)::bigint issue_count
    FROM listings l LEFT JOIN master m ON m.masterid = l.master_id
    WHERE l.master_id IS NOT NULL AND m.masterid IS NULL
    UNION ALL SELECT 'listing_details orphan', COUNT(*)
    FROM listing_details d LEFT JOIN listings l ON l.listingid = d.listing_id
    WHERE l.listingid IS NULL
    UNION ALL SELECT 'listing_media orphan', COUNT(*)
    FROM listing_media media LEFT JOIN listings l ON l.listingid = media.listing_id
    WHERE l.listingid IS NULL
    UNION ALL SELECT 'requirement locality orphan', COUNT(*)
    FROM requirements r
    CROSS JOIN LATERAL UNNEST(COALESCE(r.preferred_locality_ids, ARRAY[]::integer[])) locality(id)
    LEFT JOIN master m ON m.masterid = locality.id
    WHERE m.masterid IS NULL
    UNION ALL SELECT 'bulk-import broker orphan', COUNT(*)
    FROM bulk_import_jobs job LEFT JOIN brokers b ON b.brokerid = job.broker_id
    WHERE b.brokerid IS NULL
    UNION ALL SELECT 'embedding listing orphan', COUNT(*)
    FROM embedding_jobs job LEFT JOIN listings l ON l.listingid = job.entity_id
    WHERE LOWER(job.entity_type) = 'listing' AND l.listingid IS NULL
    UNION ALL SELECT 'embedding requirement orphan', COUNT(*)
    FROM embedding_jobs job LEFT JOIN requirements r ON r.requirementid = job.entity_id
    WHERE LOWER(job.entity_type) = 'requirement' AND r.requirementid IS NULL
    UNION ALL SELECT 'embedding invalid entity type', COUNT(*)
    FROM embedding_jobs WHERE LOWER(entity_type) NOT IN ('listing', 'requirement')
    UNION ALL SELECT 'match source-broker mismatch', COUNT(*)
    FROM matches m
    JOIN listings l ON l.listingid = m.listing_id
    JOIN requirements r ON r.requirementid = m.requirement_id
    WHERE m.listing_broker_id <> l.broker_id OR m.requirement_broker_id <> r.broker_id
    UNION ALL SELECT 'same-broker match', COUNT(*)
    FROM matches WHERE listing_broker_id = requirement_broker_id
    UNION ALL SELECT 'confirmation non-party broker', COUNT(*)
    FROM match_confirmations confirmation
    JOIN matches m ON m.matchid = confirmation.match_id
    WHERE confirmation.broker_id NOT IN (m.listing_broker_id, m.requirement_broker_id)
    UNION ALL SELECT 'connection party mismatch', COUNT(*)
    FROM match_connection_requests request
    JOIN matches m ON m.matchid = request.match_id
    WHERE request.requesting_broker_id NOT IN (m.listing_broker_id, m.requirement_broker_id)
       OR request.receiving_broker_id NOT IN (m.listing_broker_id, m.requirement_broker_id)
       OR request.requesting_broker_id = request.receiving_broker_id
    UNION ALL SELECT 'negative wallet balance', COUNT(*)
    FROM credit_wallets WHERE free_credits_balance < 0 OR paid_credits_balance < 0
    UNION ALL SELECT 'active listing missing canonical location', COUNT(*)
    FROM listings
    WHERE UPPER(COALESCE(status, '')) = 'ACTIVE'
      AND master_id IS NULL AND NULLIF(BTRIM(city), '') IS NULL
    UNION ALL SELECT 'active requirement missing canonical location', COUNT(*)
    FROM requirements
    WHERE UPPER(COALESCE(status, '')) = 'ACTIVE'
      AND COALESCE(CARDINALITY(preferred_locality_ids), 0) = 0
      AND NULLIF(BTRIM(city), '') IS NULL
    UNION ALL SELECT 'listing missing embedding', COUNT(*)
    FROM listings WHERE embedding IS NULL
    UNION ALL SELECT 'requirement missing embedding', COUNT(*)
    FROM requirements WHERE embedding IS NULL
    UNION ALL SELECT 'normalized locality duplicate groups', COUNT(*)
    FROM (
        SELECT LOWER(BTRIM(city)), LOWER(BTRIM(area))
        FROM master
        WHERE NULLIF(BTRIM(city), '') IS NOT NULL
          AND NULLIF(BTRIM(area), '') IS NOT NULL
        GROUP BY 1, 2 HAVING COUNT(*) > 1
    ) duplicates
    UNION ALL SELECT 'normalized broker phone duplicate groups', COUNT(*)
    FROM (
        SELECT RIGHT(REGEXP_REPLACE(phone_number, '\D', '', 'g'), 10)
        FROM brokers GROUP BY 1 HAVING COUNT(*) > 1
    ) duplicates
    UNION ALL SELECT 'normalized user email duplicate groups', COUNT(*)
    FROM (
        SELECT LOWER(BTRIM("Email")) FROM "Users"
        WHERE NULLIF(BTRIM("Email"), '') IS NOT NULL
        GROUP BY 1 HAVING COUNT(*) > 1
    ) duplicates
    UNION ALL SELECT 'normalized username duplicate groups', COUNT(*)
    FROM (
        SELECT LOWER(BTRIM("UserName")) FROM "Users"
        WHERE NULLIF(BTRIM("UserName"), '') IS NOT NULL
        GROUP BY 1 HAVING COUNT(*) > 1
    ) duplicates
    UNION ALL SELECT 'Aadhaar duplicate groups', COUNT(*)
    FROM (
        SELECT "AadharNumber" FROM "Users"
        WHERE "AadharNumber" IS NOT NULL GROUP BY 1 HAVING COUNT(*) > 1
    ) duplicates
    UNION ALL SELECT 'PAN duplicate groups', COUNT(*)
    FROM (
        SELECT "PanCard" FROM "Users"
        WHERE "PanCard" IS NOT NULL GROUP BY 1 HAVING COUNT(*) > 1
    ) duplicates
    UNION ALL SELECT 'payment order duplicate groups', COUNT(*)
    FROM (
        SELECT "RazorpayOrderId" FROM "PaymentTransactions"
        GROUP BY 1 HAVING COUNT(*) > 1
    ) duplicates
    UNION ALL SELECT 'payment receipt duplicate groups', COUNT(*)
    FROM (
        SELECT "Receipt" FROM "PaymentTransactions"
        GROUP BY 1 HAVING COUNT(*) > 1
    ) duplicates
    UNION ALL SELECT 'listing-requirement duplicate pairs', COUNT(*)
    FROM (
        SELECT listing_id, requirement_id FROM listing_requirements
        GROUP BY 1, 2 HAVING COUNT(*) > 1
    ) duplicates
    UNION ALL SELECT 'notification preference duplicate brokers', COUNT(*)
    FROM (
        SELECT broker_id FROM notification_preferences
        GROUP BY 1 HAVING COUNT(*) > 1
    ) duplicates
    UNION ALL SELECT 'deal duplicate matches', COUNT(*)
    FROM (
        SELECT match_id FROM deals GROUP BY 1 HAVING COUNT(*) > 1
    ) duplicates
    UNION ALL SELECT 'active connection request duplicate matches', COUNT(*)
    FROM (
        SELECT match_id FROM match_connection_requests
        WHERE status IN ('pending', 'credit_required')
        GROUP BY 1 HAVING COUNT(*) > 1
    ) duplicates
)
SELECT * FROM checks ORDER BY issue_count DESC, check_name;

SELECT p.proname,
       pg_get_function_identity_arguments(p.oid) AS arguments,
       p.prokind
FROM pg_proc p
JOIN pg_namespace n ON n.oid = p.pronamespace
WHERE n.nspname = 'public'
  AND p.proname IN ('haversine_km', 'sp_run_matching_engine')
ORDER BY p.proname, arguments;
