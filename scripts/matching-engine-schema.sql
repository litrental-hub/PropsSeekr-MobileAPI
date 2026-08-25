-- Shared schema required by the embedding Lambda and the targeted matching engine.
-- Apply this before sp_run_matching_engine.sql to every database that accepts UI or WhatsApp inventory.
CREATE EXTENSION IF NOT EXISTS vector;
CREATE EXTENSION IF NOT EXISTS pg_trgm;

CREATE OR REPLACE FUNCTION public.haversine_km(
    lat1 double precision, lon1 double precision,
    lat2 double precision, lon2 double precision)
RETURNS double precision
LANGUAGE sql
IMMUTABLE
PARALLEL SAFE
AS $$
    SELECT CASE
        WHEN lat1 IS NULL OR lon1 IS NULL OR lat2 IS NULL OR lon2 IS NULL THEN NULL
        ELSE 6371.0088 * 2 * asin(sqrt(
            power(sin(radians(lat2 - lat1) / 2), 2)
            + cos(radians(lat1)) * cos(radians(lat2))
              * power(sin(radians(lon2 - lon1) / 2), 2)))
    END;
$$;

-- The WhatsApp dataset has this locality catalogue. The UI dev database does not;
-- keep an empty compatible catalogue so matching falls back to the submitted text
-- until localities are imported.
CREATE TABLE IF NOT EXISTS public.master (
    masterid integer PRIMARY KEY,
    area text,
    city text,
    lat double precision,
    lng double precision
);

ALTER TABLE public.listings
    ADD COLUMN IF NOT EXISTS embedding vector(1536);

ALTER TABLE public.requirements
    ADD COLUMN IF NOT EXISTS embedding vector(1536);

ALTER TABLE public.matches
    ADD COLUMN IF NOT EXISTS match_tier varchar(16),
    ADD COLUMN IF NOT EXISTS score_breakdown jsonb;

CREATE UNIQUE INDEX IF NOT EXISTS ux_matches_listing_requirement
    ON public.matches (listing_id, requirement_id);
