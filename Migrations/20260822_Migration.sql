-- ============================================================
-- PropSeekr Unlock Refactor - Existing Schema Alignment
-- Date: 2026-08-22
-- Purpose: Use existing snake_case tables already present in DB
-- ============================================================

-- This script is intentionally non-destructive.
-- Tables are expected to already exist:
--   matches
--   match_confirmations
--   reveals
--   credit_wallets
--   credit_transactions
--   credit_packs
--   payments

-- ============================================================
-- Verification Queries
-- ============================================================

-- 1) Confirm required tables exist
SELECT table_name
FROM information_schema.tables
WHERE table_schema = 'public'
  AND table_name IN (
    'matches',
    'match_confirmations',
    'reveals',
    'credit_wallets',
    'credit_transactions',
    'credit_packs',
    'payments'
  )
ORDER BY table_name;

-- 2) Inspect columns for each table
SELECT table_name, column_name, data_type
FROM information_schema.columns
WHERE table_schema = 'public'
  AND table_name IN (
    'matches',
    'match_confirmations',
    'reveals',
    'credit_wallets',
    'credit_transactions',
    'credit_packs',
    'payments'
  )
ORDER BY table_name, ordinal_position;

-- 3) Optional: quick row counts
-- SELECT 'matches' AS table_name, COUNT(*) FROM public.matches
-- UNION ALL SELECT 'match_confirmations', COUNT(*) FROM public.match_confirmations
-- UNION ALL SELECT 'reveals', COUNT(*) FROM public.reveals
-- UNION ALL SELECT 'credit_wallets', COUNT(*) FROM public.credit_wallets
-- UNION ALL SELECT 'credit_transactions', COUNT(*) FROM public.credit_transactions
-- UNION ALL SELECT 'credit_packs', COUNT(*) FROM public.credit_packs
-- UNION ALL SELECT 'payments', COUNT(*) FROM public.payments;

-- ============================================================
-- End
-- ============================================================
