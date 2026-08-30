-- Precision-first matching engine for UI and WhatsApp inventory.
--
-- Hard guarantees:
--   * same broker never matches itself;
--   * transaction and property types must be compatible;
--   * both sides need a resolved city, and cities must match;
--   * configured localities must be compatible by ID, name, or the requirement radius;
--   * fixed-budget requirements need a comparable known listing price;
--   * vector similarity is used only when both vectors declare the same
--     Gemini embedding model;
--   * full rebuilds retain up to 50 matches per requirement rather than 50
--     matches globally.

ALTER TABLE public.listings
    ADD COLUMN IF NOT EXISTS embedding_model text;

ALTER TABLE public.requirements
    ADD COLUMN IF NOT EXISTS embedding_model text;

ALTER TABLE public.requirements
    ADD COLUMN IF NOT EXISTS budget_min numeric,
    ADD COLUMN IF NOT EXISTS size_max numeric,
    ADD COLUMN IF NOT EXISTS radius_km double precision,
    ADD COLUMN IF NOT EXISTS preferred_project_names text[];

CREATE OR REPLACE PROCEDURE public.sp_run_matching_engine(
    IN p_requirement_id integer DEFAULT NULL,
    IN p_listing_id integer DEFAULT NULL)
LANGUAGE plpgsql
AS $procedure$
BEGIN
    -- Rebuild only automatic matches in the requested scope. Confirmed,
    -- requested, unlocked, or otherwise progressed matches are preserved.
    DELETE FROM public.matches m
    WHERE UPPER(COALESCE(m.status, '')) = 'MATCHED'
      AND (p_requirement_id IS NULL OR m.requirement_id = p_requirement_id)
      AND (p_listing_id IS NULL OR m.listing_id = p_listing_id);

    INSERT INTO public.matches (
        listing_id,
        requirement_id,
        listing_broker_id,
        requirement_broker_id,
        match_score,
        match_tier,
        score_breakdown,
        status,
        created_at,
        status_updated_at)
    WITH requirement_base AS (
        SELECT
            r.requirementid,
            r.broker_id,
            UPPER(COALESCE(r.requirement_type, '')) AS requirement_type,
            CASE REGEXP_REPLACE(UPPER(COALESCE(r.property_type, 'ANY')), '[^A-Z0-9]', '', 'g')
                WHEN 'FLAT' THEN 'APARTMENT'
                WHEN 'FLATAPARTMENT' THEN 'APARTMENT'
                WHEN 'PENTHOUSE' THEN 'APARTMENT'
                WHEN 'INDEPENDENTHOUSE' THEN 'INDEPENDENT_HOUSE'
                WHEN 'HOUSE' THEN 'INDEPENDENT_HOUSE'
                WHEN 'VILLA' THEN 'BUNGALOW'
                WHEN 'BUNGALOWVILLA' THEN 'BUNGALOW'
                WHEN 'LAND' THEN 'PLOT'
                WHEN 'PLOTLAND' THEN 'PLOT'
                WHEN 'AGRICULTURALLAND' THEN 'AGRICULTURAL_LAND'
                WHEN 'FARMLAND' THEN 'AGRICULTURAL_LAND'
                WHEN 'OFFICESPACE' THEN 'OFFICE'
                WHEN 'COMMERCIALOFFICE' THEN 'OFFICE'
                WHEN 'COMMERCIALSPACE' THEN 'OFFICE'
                WHEN 'RETAIL' THEN 'SHOP'
                WHEN 'SHOPRETAIL' THEN 'SHOP'
                WHEN 'SHOWROOM' THEN 'SHOP'
                WHEN 'GODOWN' THEN 'WAREHOUSE'
                WHEN 'HOSTEL' THEN 'PG'
                WHEN 'PGHOSTEL' THEN 'PG'
                ELSE UPPER(COALESCE(r.property_type, 'ANY'))
            END AS property_type,
            r.configurations,
            r.budget,
            r.budget_min,
            UPPER(COALESCE(r.budget_type, '')) AS budget_type,
            UPPER(BTRIM(COALESCE(r.budget_unit, ''))) AS raw_budget_unit,
            r.size,
            r.size_max,
            COALESCE(NULLIF(r.radius_km, 0), 3.0) AS radius_km,
            r.preferred_project_names,
            CASE REGEXP_REPLACE(UPPER(COALESCE(r.furnishing_pref, 'ANY')), '[^A-Z0-9]', '', 'g')
                WHEN 'BARE' THEN 'UNFURNISHED'
                WHEN 'SEMI' THEN 'SEMI_FURNISHED'
                WHEN 'SEMIFURNISHED' THEN 'SEMI_FURNISHED'
                WHEN 'FULLYFURNISHED' THEN 'FURNISHED'
                ELSE UPPER(COALESCE(r.furnishing_pref, 'ANY'))
            END AS furnishing_pref,
            CASE REGEXP_REPLACE(UPPER(COALESCE(r.facing_pref, 'ANY')), '[^A-Z0-9]', '', 'g')
                WHEN 'NE' THEN 'NORTH_EAST'
                WHEN 'NORTHEAST' THEN 'NORTH_EAST'
                WHEN 'NW' THEN 'NORTH_WEST'
                WHEN 'NORTHWEST' THEN 'NORTH_WEST'
                WHEN 'SE' THEN 'SOUTH_EAST'
                WHEN 'SOUTHEAST' THEN 'SOUTH_EAST'
                WHEN 'SW' THEN 'SOUTH_WEST'
                WHEN 'SOUTHWEST' THEN 'SOUTH_WEST'
                ELSE UPPER(COALESCE(r.facing_pref, 'ANY'))
            END AS facing_pref,
            r.preferred_locality_ids,
            r.embedding,
            r.embedding_model,
            r.raw_message_text,
            COALESCE(NULLIF(BTRIM(r.city), ''), NULLIF(BTRIM(first_locality.city), '')) AS resolved_city,
            COALESCE(NULLIF(first_locality.geocoding_status, ''), 'pending') AS locality_geocoding_status,
            UPPER(COALESCE(r.budget_type, '')) IN ('FLEXIBLE', 'NOBUDGET') AS budget_is_flexible
        FROM public.requirements r
        LEFT JOIN public.master first_locality
            ON first_locality.masterid = r.preferred_locality_ids[1]
        WHERE UPPER(COALESCE(r.status, '')) = 'ACTIVE'
          AND COALESCE(r.isavailable, TRUE)
          AND (p_requirement_id IS NULL OR r.requirementid = p_requirement_id)
    ),
    requirement_scope AS (
        SELECT
            r.*,
            CASE
                WHEN r.raw_budget_unit IN ('PER_SQFT', 'PER SQFT', '/SQFT')
                     OR r.raw_message_text ~* '(?:/|per)[[:space:]]*sq[.]?[[:space:]]*ft|per[[:space:]]*sqft'
                    THEN 'PER_SQFT'
                WHEN r.raw_budget_unit IN ('PER_BIGHA', 'PER BIGHA', '/BIGHA') THEN 'PER_BIGHA'
                WHEN r.raw_budget_unit IN ('PER_ACRE', 'PER ACRE', '/ACRE') THEN 'PER_ACRE'
                WHEN r.raw_budget_unit IN ('PER_MONTH', 'PER MONTH', 'MONTHLY') THEN 'PER_MONTH'
                WHEN r.requirement_type IN ('RENT', 'RENTAL', 'LEASE')
                     AND r.raw_budget_unit = '' THEN 'PER_MONTH'
                ELSE 'TOTAL'
            END AS normalized_budget_unit,
            CASE
                WHEN r.raw_budget_unit IN ('LAKH', 'LAC', 'LACS', 'LAKHS') THEN r.budget * 100000
                WHEN r.raw_budget_unit IN ('CR', 'CRORE', 'CRORES') THEN r.budget * 10000000
                WHEN r.raw_budget_unit = 'K' THEN r.budget * 1000
                ELSE r.budget
            END AS normalized_budget,
            CASE
                WHEN r.raw_budget_unit IN ('LAKH', 'LAC', 'LACS', 'LAKHS') THEN r.budget_min * 100000
                WHEN r.raw_budget_unit IN ('CR', 'CRORE', 'CRORES') THEN r.budget_min * 10000000
                WHEN r.raw_budget_unit = 'K' THEN r.budget_min * 1000
                ELSE r.budget_min
            END AS normalized_budget_min
        FROM requirement_base r
        WHERE r.resolved_city IS NOT NULL
          AND (r.budget_is_flexible OR r.budget IS NOT NULL)
          AND (
              r.preferred_locality_ids IS NULL
              OR array_length(r.preferred_locality_ids, 1) IS NULL
              OR r.locality_geocoding_status IN ('resolved', 'verified')
          )
    ),
    requirement_computed AS (
        SELECT
            r.*,
            CASE
                WHEN r.budget_is_flexible THEN NULL
                WHEN r.normalized_budget_unit IN ('TOTAL', 'PER_MONTH') THEN r.normalized_budget
                WHEN r.normalized_budget_unit = 'PER_SQFT' AND r.size > 0 THEN r.normalized_budget * r.size
                WHEN r.normalized_budget_unit = 'PER_BIGHA' AND r.size > 0 THEN r.normalized_budget * (r.size / 12000.0)
                WHEN r.normalized_budget_unit = 'PER_ACRE' AND r.size > 0 THEN r.normalized_budget * (r.size / 43560.0)
                ELSE NULL
            END AS computed_budget,
            CASE
                WHEN r.normalized_budget_min IS NULL THEN NULL
                WHEN r.normalized_budget_unit IN ('TOTAL', 'PER_MONTH') THEN r.normalized_budget_min
                WHEN r.normalized_budget_unit = 'PER_SQFT' AND r.size > 0 THEN r.normalized_budget_min * r.size
                WHEN r.normalized_budget_unit = 'PER_BIGHA' AND r.size > 0 THEN r.normalized_budget_min * (r.size / 12000.0)
                WHEN r.normalized_budget_unit = 'PER_ACRE' AND r.size > 0 THEN r.normalized_budget_min * (r.size / 43560.0)
                ELSE NULL
            END AS computed_budget_min
        FROM requirement_scope r
    ),
    listing_base AS (
        SELECT
            l.listingid,
            l.broker_id,
            l.master_id,
            UPPER(COALESCE(l.listing_type, '')) AS listing_type,
            CASE REGEXP_REPLACE(UPPER(COALESCE(l.property_type, '')), '[^A-Z0-9]', '', 'g')
                WHEN 'FLAT' THEN 'APARTMENT'
                WHEN 'FLATAPARTMENT' THEN 'APARTMENT'
                WHEN 'PENTHOUSE' THEN 'APARTMENT'
                WHEN 'INDEPENDENTHOUSE' THEN 'INDEPENDENT_HOUSE'
                WHEN 'HOUSE' THEN 'INDEPENDENT_HOUSE'
                WHEN 'VILLA' THEN 'BUNGALOW'
                WHEN 'BUNGALOWVILLA' THEN 'BUNGALOW'
                WHEN 'LAND' THEN 'PLOT'
                WHEN 'PLOTLAND' THEN 'PLOT'
                WHEN 'AGRICULTURALLAND' THEN 'AGRICULTURAL_LAND'
                WHEN 'FARMLAND' THEN 'AGRICULTURAL_LAND'
                WHEN 'OFFICESPACE' THEN 'OFFICE'
                WHEN 'COMMERCIALOFFICE' THEN 'OFFICE'
                WHEN 'COMMERCIALSPACE' THEN 'OFFICE'
                WHEN 'RETAIL' THEN 'SHOP'
                WHEN 'SHOPRETAIL' THEN 'SHOP'
                WHEN 'SHOWROOM' THEN 'SHOP'
                WHEN 'GODOWN' THEN 'WAREHOUSE'
                WHEN 'HOSTEL' THEN 'PG'
                WHEN 'PGHOSTEL' THEN 'PG'
                ELSE UPPER(COALESCE(l.property_type, ''))
            END AS property_type,
            l.configuration,
            l.price,
            UPPER(BTRIM(COALESCE(l.price_unit, ''))) AS raw_price_unit,
            l.size,
            CASE REGEXP_REPLACE(UPPER(COALESCE(l.furnishing, '')), '[^A-Z0-9]', '', 'g')
                WHEN 'BARE' THEN 'UNFURNISHED'
                WHEN 'SEMI' THEN 'SEMI_FURNISHED'
                WHEN 'SEMIFURNISHED' THEN 'SEMI_FURNISHED'
                WHEN 'FULLYFURNISHED' THEN 'FURNISHED'
                ELSE UPPER(COALESCE(l.furnishing, ''))
            END AS furnishing,
            CASE REGEXP_REPLACE(UPPER(COALESCE(l.facing, '')), '[^A-Z0-9]', '', 'g')
                WHEN 'NE' THEN 'NORTH_EAST'
                WHEN 'NORTHEAST' THEN 'NORTH_EAST'
                WHEN 'NW' THEN 'NORTH_WEST'
                WHEN 'NORTHWEST' THEN 'NORTH_WEST'
                WHEN 'SE' THEN 'SOUTH_EAST'
                WHEN 'SOUTHEAST' THEN 'SOUTH_EAST'
                WHEN 'SW' THEN 'SOUTH_WEST'
                WHEN 'SOUTHWEST' THEN 'SOUTH_WEST'
                ELSE UPPER(COALESCE(l.facing, ''))
            END AS facing,
            l.project_name,
            l.embedding,
            l.embedding_model,
            l.raw_message_text,
            l.price_status,
            locality.area AS listing_area,
            locality.city AS master_city,
            locality.lat AS listing_lat,
            locality.lng AS listing_lng,
            COALESCE(NULLIF(locality.geocoding_status, ''), 'pending') AS locality_geocoding_status,
            COALESCE(NULLIF(BTRIM(l.city), ''), NULLIF(BTRIM(locality.city), '')) AS resolved_city
        FROM public.listings l
        LEFT JOIN public.master locality ON locality.masterid = l.master_id
        WHERE UPPER(COALESCE(l.status, '')) = 'ACTIVE'
          AND COALESCE(l.isavailable, TRUE)
          AND (p_listing_id IS NULL OR l.listingid = p_listing_id)
    ),
    listing_scope AS (
        SELECT
            l.*,
            CASE
                WHEN l.raw_price_unit IN ('PER_SQFT', 'PER SQFT', '/SQFT')
                     OR l.raw_message_text ~* '(?:/|per)[[:space:]]*sq[.]?[[:space:]]*ft|per[[:space:]]*sqft'
                    THEN 'PER_SQFT'
                WHEN l.raw_price_unit IN ('PER_BIGHA', 'PER BIGHA', '/BIGHA') THEN 'PER_BIGHA'
                WHEN l.raw_price_unit IN ('PER_ACRE', 'PER ACRE', '/ACRE') THEN 'PER_ACRE'
                WHEN l.raw_price_unit IN ('PER_MONTH', 'PER MONTH', 'MONTHLY') THEN 'PER_MONTH'
                WHEN l.listing_type IN ('RENT', 'RENTAL', 'LEASE')
                     AND l.raw_price_unit = '' THEN 'PER_MONTH'
                ELSE 'TOTAL'
            END AS normalized_price_unit,
            CASE
                WHEN l.raw_price_unit IN ('LAKH', 'LAC', 'LACS', 'LAKHS') THEN l.price * 100000
                WHEN l.raw_price_unit IN ('CR', 'CRORE', 'CRORES') THEN l.price * 10000000
                WHEN l.raw_price_unit = 'K' THEN l.price * 1000
                ELSE l.price
            END AS normalized_price
        FROM listing_base l
        WHERE l.resolved_city IS NOT NULL
          AND l.master_id IS NOT NULL
          AND l.locality_geocoding_status IN ('resolved', 'verified')
    ),
    listing_computed AS (
        SELECT
            l.*,
            CASE
                WHEN l.normalized_price_unit IN ('TOTAL', 'PER_MONTH') THEN l.normalized_price
                WHEN l.normalized_price_unit = 'PER_SQFT' AND l.size > 0 THEN l.normalized_price * l.size
                WHEN l.normalized_price_unit = 'PER_BIGHA' AND l.size > 0 THEN l.normalized_price * (l.size / 12000.0)
                WHEN l.normalized_price_unit = 'PER_ACRE' AND l.size > 0 THEN l.normalized_price * (l.size / 43560.0)
                ELSE NULL
            END AS computed_price
        FROM listing_scope l
    ),
    candidates AS (
        SELECT
            l.listingid,
            l.broker_id AS listing_broker_id,
            r.requirementid,
            r.broker_id AS requirement_broker_id,
            l.embedding AS listing_embedding,
            r.embedding AS requirement_embedding,
            l.embedding_model AS listing_embedding_model,
            r.embedding_model AS requirement_embedding_model,
            l.normalized_price,
            r.normalized_budget,
            r.normalized_budget_min,
            l.normalized_price_unit,
            r.normalized_budget_unit,
            l.computed_price,
            r.computed_budget,
            r.computed_budget_min,
            r.budget_is_flexible,
            l.size AS listing_size,
            r.size AS requirement_size,
            r.size_max AS requirement_size_max,
            l.configuration AS listing_configuration,
            r.configurations AS requirement_configurations,
            l.furnishing AS listing_furnishing,
            r.furnishing_pref,
            l.facing AS listing_facing,
            r.facing_pref,
            l.project_name AS listing_project_name,
            r.preferred_project_names,
            r.radius_km,
            l.master_id AS listing_master_id,
            l.listing_area,
            l.resolved_city AS listing_city,
            r.resolved_city AS requirement_city,
            preferred.area AS preferred_area,
            preferred.distance_km,
            preferred.locality_similarity,
            preferred.is_exact
        FROM listing_computed l
        CROSS JOIN requirement_computed r
        LEFT JOIN LATERAL (
            SELECT
                pm.area,
                public.haversine_km(pm.lat, pm.lng, l.listing_lat, l.listing_lng) AS distance_km,
                similarity(LOWER(COALESCE(pm.area, '')), LOWER(COALESCE(l.listing_area, ''))) AS locality_similarity,
                pm.masterid = l.master_id AS is_exact
            FROM public.master pm
            WHERE pm.masterid = ANY(r.preferred_locality_ids)
              AND COALESCE(NULLIF(pm.geocoding_status, ''), 'pending') IN ('resolved', 'verified')
            ORDER BY
                (pm.masterid = l.master_id) DESC,
                similarity(LOWER(COALESCE(pm.area, '')), LOWER(COALESCE(l.listing_area, ''))) DESC,
                public.haversine_km(pm.lat, pm.lng, l.listing_lat, l.listing_lng) ASC NULLS LAST
            LIMIT 1
        ) preferred ON TRUE
        WHERE l.broker_id <> r.broker_id
          AND LOWER(BTRIM(l.resolved_city)) = LOWER(BTRIM(r.resolved_city))
          AND (
              (r.requirement_type = 'BUY' AND l.listing_type IN ('SELL', 'SALE'))
              OR (r.requirement_type IN ('RENT', 'RENTAL') AND l.listing_type IN ('RENT', 'RENTAL'))
              OR (r.requirement_type = 'LEASE' AND l.listing_type = 'LEASE')
          )
          AND (
              r.property_type = 'ANY'
              OR l.property_type = r.property_type
              OR (r.property_type IN ('FLAT', 'APARTMENT') AND l.property_type IN ('FLAT', 'APARTMENT', 'PENTHOUSE'))
              OR (r.property_type IN ('INDEPENDENT_HOUSE', 'BUNGALOW') AND l.property_type IN ('INDEPENDENT_HOUSE', 'BUNGALOW', 'DUPLEX'))
              OR (r.property_type IN ('OFFICE', 'COMMERCIAL_SPACE') AND l.property_type IN ('OFFICE', 'COMMERCIAL_SPACE'))
              OR (r.property_type IN ('SHOP', 'RETAIL') AND l.property_type IN ('SHOP', 'RETAIL', 'SHOWROOM'))
              OR (r.property_type IN ('GODOWN', 'WAREHOUSE') AND l.property_type IN ('GODOWN', 'WAREHOUSE'))
          )
          AND (
              r.configurations IS NULL
              OR array_length(r.configurations, 1) IS NULL
              OR (
                  NULLIF(BTRIM(l.configuration), '') IS NOT NULL
                  AND EXISTS (
                      SELECT 1
                      FROM unnest(r.configurations) required_configuration
                      WHERE REGEXP_REPLACE(UPPER(BTRIM(required_configuration)), '[^A-Z0-9]', '', 'g')
                            = REGEXP_REPLACE(UPPER(BTRIM(l.configuration)), '[^A-Z0-9]', '', 'g')
                  )
              )
          )
          AND (
              r.preferred_locality_ids IS NULL
              OR array_length(r.preferred_locality_ids, 1) IS NULL
              OR preferred.is_exact
              OR preferred.locality_similarity >= 0.60
              OR preferred.distance_km <= r.radius_km
          )
          AND (
              r.budget_is_flexible
              OR (
                  l.normalized_price IS NOT NULL
                  AND r.normalized_budget IS NOT NULL
                  AND (
                      (l.normalized_price_unit = r.normalized_budget_unit
                       AND l.normalized_price <= r.normalized_budget * 1.10)
                      OR (l.computed_price IS NOT NULL AND r.computed_budget IS NOT NULL
                          AND l.computed_price <= r.computed_budget * 1.10)
                  )
              )
          )
    ),
    scored AS (
        SELECT
            c.*,
            CASE
                WHEN c.is_exact THEN 25
                WHEN c.locality_similarity >= 0.80 THEN 23
                WHEN c.locality_similarity >= 0.60 THEN 20
                WHEN c.distance_km <= 1.0 THEN 22
                WHEN c.distance_km <= 2.0 THEN 18
                WHEN c.distance_km <= c.radius_km THEN 14
                ELSE 10
            END::numeric AS location_score,
            15::numeric AS property_type_score,
            CASE
                WHEN c.budget_is_flexible
                     AND c.normalized_budget IS NOT NULL
                     AND c.normalized_price_unit = c.normalized_budget_unit
                     AND c.normalized_price <= c.normalized_budget THEN 17
                WHEN c.budget_is_flexible
                     AND c.normalized_budget IS NOT NULL
                     AND c.normalized_price_unit = c.normalized_budget_unit
                     AND c.normalized_price <= c.normalized_budget * 1.25 THEN 13
                WHEN c.budget_is_flexible THEN 12
                WHEN c.normalized_price_unit = c.normalized_budget_unit
                     AND (c.normalized_budget_min IS NULL OR c.normalized_price >= c.normalized_budget_min)
                     AND c.normalized_price <= c.normalized_budget THEN 20
                WHEN c.computed_price IS NOT NULL AND c.computed_budget IS NOT NULL
                     AND (c.computed_budget_min IS NULL OR c.computed_price >= c.computed_budget_min)
                     AND c.computed_price <= c.computed_budget THEN 20
                WHEN c.normalized_budget_min IS NOT NULL
                     AND c.normalized_price_unit = c.normalized_budget_unit
                     AND c.normalized_price < c.normalized_budget_min THEN 15
                WHEN c.normalized_price_unit = c.normalized_budget_unit
                     AND c.normalized_price <= c.normalized_budget * 1.10 THEN 13
                WHEN c.computed_price IS NOT NULL AND c.computed_budget IS NOT NULL
                     AND c.computed_price <= c.computed_budget * 1.10 THEN 13
                ELSE 6
            END::numeric AS price_score,
            CASE
                WHEN c.requirement_size IS NULL OR c.listing_size IS NULL THEN 5
                WHEN c.listing_size >= c.requirement_size
                     AND (c.requirement_size_max IS NULL OR c.listing_size <= c.requirement_size_max) THEN 10
                WHEN c.listing_size >= c.requirement_size * 0.90
                     AND (c.requirement_size_max IS NULL OR c.listing_size <= c.requirement_size_max * 1.10) THEN 7
                WHEN c.listing_size >= c.requirement_size * 0.75
                     AND (c.requirement_size_max IS NULL OR c.listing_size <= c.requirement_size_max * 1.25) THEN 3
                ELSE 0
            END::numeric AS size_score,
            CASE
                WHEN c.requirement_configurations IS NULL
                     OR array_length(c.requirement_configurations, 1) IS NULL THEN 5
                WHEN c.listing_configuration IS NULL THEN 2
                WHEN EXISTS (
                    SELECT 1
                    FROM unnest(c.requirement_configurations) configuration
                    WHERE UPPER(BTRIM(configuration)) = UPPER(BTRIM(c.listing_configuration))
                ) THEN 10
                ELSE 0
            END::numeric AS configuration_score,
            CASE
                WHEN c.furnishing_pref IN ('', 'ANY') THEN 5
                WHEN c.listing_furnishing = c.furnishing_pref THEN 5
                WHEN c.furnishing_pref IN ('SEMI', 'SEMI_FURNISHED')
                     AND c.listing_furnishing IN ('SEMI', 'SEMI_FURNISHED', 'FURNISHED', 'FULLY_FURNISHED') THEN 4
                ELSE 0
            END::numeric AS furnishing_score,
            CASE
                WHEN c.facing_pref IN ('', 'ANY') THEN 2
                WHEN c.listing_facing = c.facing_pref THEN 2
                ELSE 0
            END::numeric AS facing_score,
            CASE
                WHEN c.preferred_project_names IS NULL
                     OR array_length(c.preferred_project_names, 1) IS NULL THEN 3
                WHEN NULLIF(BTRIM(c.listing_project_name), '') IS NULL THEN 0
                WHEN EXISTS (
                    SELECT 1
                    FROM unnest(c.preferred_project_names) project_name
                    WHERE LOWER(BTRIM(project_name)) = LOWER(BTRIM(c.listing_project_name))
                ) THEN 3
                WHEN EXISTS (
                    SELECT 1
                    FROM unnest(c.preferred_project_names) project_name
                    WHERE similarity(LOWER(BTRIM(project_name)), LOWER(BTRIM(c.listing_project_name))) >= 0.65
                ) THEN 2
                ELSE 0
            END::numeric AS project_score,
            CASE
                WHEN c.listing_embedding IS NULL OR c.requirement_embedding IS NULL THEN 0
                WHEN c.listing_embedding_model IS DISTINCT FROM 'gemini-embedding-001' THEN 0
                WHEN c.requirement_embedding_model IS DISTINCT FROM c.listing_embedding_model THEN 0
                ELSE ROUND(GREATEST(0, (1 - (c.listing_embedding <=> c.requirement_embedding)) * 10)::numeric, 2)
            END::numeric AS vector_score
        FROM candidates c
    ),
    ranked AS (
        SELECT
            s.*,
            LEAST(100, ROUND(
                s.location_score
                + s.property_type_score
                + s.price_score
                + s.size_score
                + s.configuration_score
                + s.furnishing_score
                + s.facing_score
                + s.project_score
                + s.vector_score,
                2)) AS match_score
        FROM scored s
    ),
    limited AS (
        SELECT
            r.*,
            ROW_NUMBER() OVER (PARTITION BY r.requirementid ORDER BY r.match_score DESC, r.listingid DESC) AS requirement_rank,
            ROW_NUMBER() OVER (PARTITION BY r.listingid ORDER BY r.match_score DESC, r.requirementid DESC) AS listing_rank
        FROM ranked r
        WHERE r.match_score >= 35
    )
    SELECT
        listingid,
        requirementid,
        listing_broker_id,
        requirement_broker_id,
        match_score,
        CASE
            WHEN match_score >= 80 THEN 'TIER1'
            WHEN match_score >= 60 THEN 'TIER2'
            ELSE 'TIER3'
        END,
        jsonb_build_object(
            'location_score', location_score,
            'property_type_score', property_type_score,
            'price_score', price_score,
            'size_score', size_score,
            'configuration_score', configuration_score,
            'furnishing_score', furnishing_score,
            'facing_score', facing_score,
            'project_score', project_score,
            'vector_score', vector_score,
            'distance_km', distance_km,
            'locality_similarity', locality_similarity,
            'listing_area', listing_area,
            'preferred_area', preferred_area,
            'listing_city', listing_city,
            'requirement_city', requirement_city,
            'listing_price', normalized_price,
            'requirement_budget', normalized_budget,
            'requirement_budget_min', normalized_budget_min,
            'listing_price_unit', normalized_price_unit,
            'requirement_budget_unit', normalized_budget_unit,
            'requirement_radius_km', radius_km,
            'listing_embedding_model', listing_embedding_model,
            'requirement_embedding_model', requirement_embedding_model),
        'MATCHED',
        NOW(),
        NOW()
    FROM limited
    WHERE
        CASE
            WHEN p_requirement_id IS NOT NULL AND p_listing_id IS NULL THEN requirement_rank <= 50
            WHEN p_listing_id IS NOT NULL AND p_requirement_id IS NULL THEN listing_rank <= 50
            ELSE requirement_rank <= 50
        END
    ON CONFLICT (listing_id, requirement_id) DO UPDATE
    SET match_score = EXCLUDED.match_score,
        match_tier = EXCLUDED.match_tier,
        score_breakdown = EXCLUDED.score_breakdown,
        status = 'MATCHED',
        status_updated_at = NOW();
END;
$procedure$;
