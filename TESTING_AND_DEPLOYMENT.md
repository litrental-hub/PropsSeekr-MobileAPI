# Unlock Functionality Refactor - Testing & Deployment Guide

## Requirement accuracy rollout (2026-08-28)

The richer Add Property/Add Requirement contract requires an ordered database rollout. Do not restart the updated API against the old view schema.

1. Back up the target database and confirm the intended connection from the deployment secret store.
2. Apply EF migration `20260828000200_AddRequirementMatchingPreferences`. It adds `budget_min`, `size_max`, `radius_km`, and `preferred_project_names` to `requirements_table` and appends them to the active `requirements` view.
3. Apply `scripts/harden-matching-engine.sql` explicitly. This installs historical alias normalization, per-requirement radius, range-aware size/budget scoring, facing, and preferred-project scoring.
4. Verify the four columns on both `requirements_table` and the `requirements` view, then inspect `pg_get_functiondef('public.sp_run_matching_engine(integer,integer)'::regprocedure)` for `radius_km`, `facing_score`, and `project_score`.
5. Restart the API only after steps 2-4 succeed. Create one controlled listing/requirement pair from different test brokers and inspect `matches.score_breakdown`.
6. Sample targeted matching before any full rebuild. The procedure preserves progressed matches, but a full run can legitimately replace automatic `MATCHED` rows under the improved scoring rules.

New writes are normalized in the API and historical aliases are normalized inside the procedure. No destructive historical property-type backfill is required for this release.

## Quick Summary

The unlock functionality has been completely refactored from using `PropertyRequestId` to using `MatchId` with a dual handshake confirmation flow and credit wallet system.

**Key Changes:**
- ✅ Deprecated `propertyRequestId` - replaced with `matchId`
- ✅ Added dual handshake pre-reveal confirmation
- ✅ Implemented immutable credit ledger system
- ✅ Added credit wallet management
- ✅ 7 new database tables with proper constraints
- ✅ New service: `UnlockService` with IUnlockService interface
- ✅ New controller endpoints: `/matches/{id}/confirm` and `/matches/{id}/reveal`
- ✅ Backward compatibility maintained (legacy `/unlock` endpoint still works)

**Status:** ✅ Code complete and builds successfully | ⏳ Database migration pending | ⏳ End-to-end testing pending

---

## Part 1: Apply Database Migration

### Option A: Using Entity Framework CLI (Recommended)
```bash
cd "c:\Users\Aman Jain\source\repos\PropsSeekr-MobileAPI"

# Method 1: Direct database update
dotnet ef database update --connection "<connection string from your deployment secret store>"

# Method 2: Using PowerShell (Windows)
$conn = "<connection string from your deployment secret store>"
dotnet ef database update --connection $conn
```

### Option B: Using SQL Script Directly (If EF CLI fails)
```bash
# Execute the SQL migration manually using psql or pgAdmin
psql -h propseekr-db.cveo6kcqsisw.ap-south-1.rds.amazonaws.com \
     -p 5432 \
     -U postgres \
     -d PropSeekr \
     -f Migrations/20260822_Migration.sql

# Enter the password from your deployment secret store when prompted.
```

### Option C: Using DBeaver or pgAdmin
1. Connect to: `propseekr-db.cveo6kcqsisw.ap-south-1.rds.amazonaws.com:5432`
2. Database: `PropSeekr`
3. User: `postgres`
4. Password: use the value from your deployment secret store
5. Open `Migrations/20260822_Migration.sql`
6. Execute all statements

### Verify Migration Success
```sql
-- Check if all 7 new tables were created
SELECT table_name FROM information_schema.tables 
WHERE table_schema = 'public' 
AND table_name IN ('Matches', 'MatchConfirmations', 'Reveals', 
                   'CreditWallets', 'CreditTransactions', 'CreditPacks', 'Payments');

-- Should return 7 rows:
-- Matches
-- MatchConfirmations
-- Reveals
-- CreditWallets
-- CreditTransactions
-- CreditPacks
-- Payments

-- Check migration history
SELECT * FROM "__EFMigrationsHistory" 
ORDER BY "MigrationId" DESC LIMIT 3;
-- Should include: 20260822_AddDualHandshakeAndCreditSystem
```

---

## Part 2: Initialize Credit Wallets for Existing Users

After migration, run this SQL to give existing users free credits:

```sql
-- Initialize wallets for existing users
INSERT INTO "CreditWallets" ("Id", "UserId", "FreeCreditsBalance", "PaidCreditsBalance", "FreeCreditsResetAt", "CreatedAt", "UpdatedAt")
SELECT 
    gen_random_uuid(),
    "Id",
    5,  -- 5 free unlock credits
    0,
    NOW() + INTERVAL '30 days',
    NOW(),
    NOW()
FROM "Users"
WHERE "Id" NOT IN (SELECT "UserId" FROM "CreditWallets")
ON CONFLICT ("UserId") DO NOTHING;

-- Log the grants
INSERT INTO "CreditTransactions" ("UserId", "Type", "Amount", "BalanceAfter", "ReferenceType", "Notes", "CreatedAt")
SELECT 
    "UserId",
    'grant',
    5,
    5,
    'monthly_grant',
    'Initial free credits grant',
    NOW()
FROM "CreditWallets"
WHERE "UserId" NOT IN (SELECT DISTINCT "UserId" FROM "CreditTransactions" WHERE "Type" = 'grant')
ORDER BY "UserId";
```

---

## Part 3: Build & Deploy Application

```bash
cd "c:\Users\Aman Jain\source\repos\PropsSeekr-MobileAPI"

# Build
dotnet build

# Publish
dotnet publish -c Release -o ./publish

# Or run locally for testing
dotnet run
```

---

## Part 4: End-to-End Testing

### Prerequisite: Create Test Data in Database

```sql
-- Create test users (if they don't exist)
INSERT INTO "Users" ("Id", "Name", "Email", "MobileNumber", "PasswordHash", "IsVerified", "CreatedDate")
VALUES 
    ('11111111-1111-1111-1111-111111111111'::uuid, 'Broker A', 'broker.a@test.com', '9999999991', 'hash1', true, now()),
    ('22222222-2222-2222-2222-222222222222'::uuid, 'Broker B', 'broker.b@test.com', '9999999992', 'hash2', true, now())
ON CONFLICT DO NOTHING;

-- Create test property requests
INSERT INTO "PropertyRequests" ("Id", "UserId", "ListingType", "TransactionType", "Category", "PropertyType", "Price", "BudgetMin", "BudgetMax", "Locality", "City", "Status", "PostedAt", "RadiusKm")
VALUES 
    ('33333333-3333-3333-3333-333333333333'::uuid, '11111111-1111-1111-1111-111111111111'::uuid, 'SUPPLY', 'SELL', 'PROPERTY', 'RESIDENTIAL', 5000000, NULL, NULL, 'Vijay Nagar', 'Indore', 'ACTIVE', now(), 5),
    ('44444444-4444-4444-4444-444444444444'::uuid, '22222222-2222-2222-2222-222222222222'::uuid, 'DEMAND', 'BUY', 'PROPERTY', 'RESIDENTIAL', NULL, 4000000, 6000000, 'Vijay Nagar', 'Indore', 'ACTIVE', now(), 5)
ON CONFLICT DO NOTHING;

-- Create a test match
INSERT INTO "Matches" ("Id", "ListingId", "RequirementId", "MatchScore", "State", "CreatedAt", "UpdatedAt")
VALUES 
    ('55555555-5555-5555-5555-555555555555'::uuid, 
     '33333333-3333-3333-3333-333333333333'::uuid,
     '44444444-4444-4444-4444-444444444444'::uuid,
     85.5,
     'matched',
     now(),
     now())
ON CONFLICT DO NOTHING;

-- Verify test data
SELECT * FROM "Matches" LIMIT 1;
SELECT * FROM "CreditWallets" WHERE "UserId" IN ('11111111-1111-1111-1111-111111111111', '22222222-2222-2222-2222-222222222222');
```

### Test 1: Broker Confirmation (Dual Handshake)

**Endpoint:** `POST /api/v1/user-matches/matches/{matchId}/confirm`

**Request Header:**
```
Authorization: Bearer <JWT_TOKEN_FOR_BROKER_A>
Content-Type: application/json
```

**Request Body:**
```json
{
  "matchId": "55555555-5555-5555-5555-555555555555",
  "availabilityConfirmed": true,
  "priceValid": true,
  "priceNegotiable": false,
  "readyToConnect": true
}
```

**Expected Response (Waiting for counterparty):**
```json
{
  "success": true,
  "message": "Confirmation recorded. Waiting for counterparty.",
  "matchId": "55555555-5555-5555-5555-555555555555",
  "state": "pending_confirmation",
  "windowExpiresAt": "2026-08-23T14:30:00Z",
  "creditsRequired": 1
}
```

**Repeat for Broker B with Authorization: Bearer <JWT_TOKEN_FOR_BROKER_B>**

**Expected Response (Both confirmed):**
```json
{
  "success": true,
  "message": "Match confirmed by both brokers. Ready to reveal contacts.",
  "matchId": "55555555-5555-5555-5555-555555555555",
  "state": "confirmed",
  "windowExpiresAt": "2026-08-23T14:30:00Z",
  "creditsRequired": 1
}
```

**Verify in Database:**
```sql
SELECT * FROM "MatchConfirmations" WHERE "MatchId" = '55555555-5555-5555-5555-555555555555';
-- Should show 2 rows, both with ConfirmedAt set

SELECT "State" FROM "Matches" WHERE "Id" = '55555555-5555-5555-5555-555555555555';
-- Should return: confirmed
```

### Test 2: Reveal/Unlock (Credit Deduction)

**Endpoint:** `POST /api/v1/user-matches/matches/{matchId}/reveal`

**Request Header:**
```
Authorization: Bearer <JWT_TOKEN_FOR_BROKER_A>
Content-Type: application/json
```

**Request Body:**
```json
{
  "matchId": "55555555-5555-5555-5555-555555555555"
}
```

**Expected Response:**
```json
{
  "success": true,
  "message": "Contact details unlocked successfully!",
  "creditsRemaining": 4,
  "unlockedContact": {
    "ownerName": "Broker B",
    "ownerMobile": "9999999992",
    "ownerEmail": "broker.b@test.com"
  }
}
```

**Verify in Database:**
```sql
-- Check reveal created
SELECT * FROM "Reveals" WHERE "MatchId" = '55555555-5555-5555-5555-555555555555';
-- Should show 1 row with RevealedAt set to current time

-- Check credit deduction
SELECT * FROM "CreditTransactions" 
WHERE "UserId" = '11111111-1111-1111-1111-111111111111' 
ORDER BY "CreatedAt" DESC LIMIT 1;
-- Should show: type='deduct', amount=1, balance_after=4, reference_type='reveal'

-- Check wallet updated
SELECT * FROM "CreditWallets" 
WHERE "UserId" = '11111111-1111-1111-1111-111111111111';
-- Should show: free_credits_balance=4, paid_credits_balance=0
```

### Test 3: Idempotency (Second Call Returns Cached Result)

**Same endpoint call again:** `POST /api/v1/user-matches/matches/{matchId}/reveal`

**Expected Response:**
```json
{
  "success": true,
  "message": "Contact details already unlocked.",
  "creditsRemaining": 4,
  "unlockedContact": {
    "ownerName": "Broker B",
    "ownerMobile": "9999999992",
    "ownerEmail": "broker.b@test.com"
  }
}
```

**Verify in Database:**
```sql
-- Count reveals (should still be 1)
SELECT COUNT(*) FROM "Reveals" WHERE "MatchId" = '55555555-5555-5555-5555-555555555555';
-- Should return: 1

-- Count credit transactions (should still be 1)
SELECT COUNT(*) FROM "CreditTransactions" 
WHERE "UserId" = '11111111-1111-1111-1111-111111111111' 
AND "ReferenceType" = 'reveal';
-- Should return: 1 (no double deduction)

-- Wallet balance unchanged
SELECT "FreeCreditsBalance" FROM "CreditWallets" 
WHERE "UserId" = '11111111-1111-1111-1111-111111111111';
-- Should still be: 4
```

### Test 4: Insufficient Credits

**Create new match, then deplete credits:**

```sql
-- Deplete Broker A's credits
UPDATE "CreditWallets" 
SET "FreeCreditsBalance" = 0, "PaidCreditsBalance" = 0
WHERE "UserId" = '11111111-1111-1111-1111-111111111111';
```

**Call reveal endpoint:**

**Expected Response:**
```json
{
  "success": false,
  "message": "Insufficient credits. Required: 1, Available: 0",
  "creditsRemaining": 0
}
```

### Test 5: Match Not Confirmed (Invalid State)

**Create new match without confirmation:**

```sql
INSERT INTO "Matches" ("Id", "ListingId", "RequirementId", "MatchScore", "State")
VALUES ('66666666-6666-6666-6666-666666666666'::uuid, 
        '33333333-3333-3333-3333-333333333333'::uuid,
        '44444444-4444-4444-4444-444444444444'::uuid,
        85.5,
        'matched');  -- Not confirmed!
```

**Call reveal with this match:**

**Expected Response:**
```json
{
  "success": false,
  "message": "Match state is 'matched', not 'confirmed'. Cannot reveal."
}
```

---

## Part 5: Monitoring & Audit

### Key Queries for Operations Team

**Recent Credit Transactions:**
```sql
SELECT u."Name", ct."Type", ct."Amount", ct."BalanceAfter", ct."CreatedAt"
FROM "CreditTransactions" ct
JOIN "Users" u ON ct."UserId" = u."Id"
ORDER BY ct."CreatedAt" DESC
LIMIT 20;
```

**User Credit Balances:**
```sql
SELECT u."Name", u."Email", 
       cw."FreeCreditsBalance", cw."PaidCreditsBalance",
       (cw."FreeCreditsBalance" + cw."PaidCreditsBalance") AS "TotalBalance"
FROM "CreditWallets" cw
JOIN "Users" u ON cw."UserId" = u."Id"
ORDER BY (cw."FreeCreditsBalance" + cw."PaidCreditsBalance") DESC;
```

**Match Statistics:**
```sql
SELECT "State", COUNT(*) AS "Count"
FROM "Matches"
GROUP BY "State"
ORDER BY "Count" DESC;

-- Breakdown by state:
-- matched: 45
-- pending_confirmation: 12
-- confirmed: 8
-- expired: 3
```

**Reveal Performance:**
```sql
SELECT COUNT(*) AS "TotalReveals", 
       COUNT(DISTINCT "MatchId") AS "UniqueMatches",
       MIN("RevealedAt") AS "FirstReveal",
       MAX("RevealedAt") AS "LastReveal"
FROM "Reveals";
```

**Payment Processing:**
```sql
SELECT "Status", COUNT(*) AS "Count", SUM("Amount") AS "TotalAmount"
FROM "Payments"
GROUP BY "Status"
ORDER BY "Count" DESC;
```

---

## Part 6: Troubleshooting

### Problem: "Match not found"
- Verify MatchId exists in database
- Check if user is testing with correct match that's in system

### Problem: "User is not a party to this match"
- Verify JWT token is for one of the brokers (listing or requirement owner)
- MatchId should reference listing/requirement created by that user

### Problem: "Insufficient credits"
- Check user's CreditWallet balance
- Run: `SELECT * FROM "CreditWallets" WHERE "UserId" = '<uuid>';`
- If missing, run initialization SQL from Part 2

### Problem: "Match state is 'matched', not 'confirmed'"
- Both brokers must call `/confirm` endpoint first
- Check `MatchConfirmations` table - should have 2 rows, both with ConfirmedAt set
- Wait 24 hours or manually update `Matches.State` to 'confirmed' for testing

### Problem: "Contact details already unlocked" on first call
- Reveal record already exists in database
- This is expected behavior (idempotency)
- Verify `Reveals` table has entry for this matchId

---

## Part 7: Performance Baseline

Expected response times after optimization:
- **Confirm endpoint:** < 100ms (simple update)
- **Reveal endpoint:** < 150ms (2 transactions: deduction + reveal record)
- **Credit check:** < 50ms (wallet lookup by index)

If exceeding these, check:
- Database connection pooling
- Index health: `ANALYZE "CreditWallets";`
- Table bloat: `VACUUM ANALYZE;`

---

## Part 8: Mobile App Integration

### Updated Request/Response Contracts

**Breaking Change:**
```javascript
// OLD (DEPRECATED)
POST /api/v1/user-matches/unlock
{ "propertyRequestId": "guid" }

// NEW
POST /api/v1/user-matches/matches/{matchId}/confirm
{ "matchId": "guid", "availabilityConfirmed": true, ... }

POST /api/v1/user-matches/matches/{matchId}/reveal
{ "matchId": "guid" }
```

### UI/UX Flow

1. **Get Matches:** `GET /api/v1/user-matches`
   - Response now includes `matchId` for each match item

2. **User Clicks "Unlock":**
   - Show confirmation checklist dialog
   - Call `POST /matches/{matchId}/confirm` with checklist values

3. **Wait for Counterparty:**
   - Poll `GET /matches/{matchId}` status (or show message)
   - Or receive push notification when both confirmed

4. **Call Reveal:**
   - Call `POST /matches/{matchId}/reveal`
   - Display returned contact details
   - Update UI to show "CONFIRMED" status

---

## Part 9: Rollback Plan

If issues arise, rollback is straightforward:

```sql
-- Drop new tables (if needed)
DROP TABLE IF EXISTS "Payments";
DROP TABLE IF EXISTS "CreditPacks";
DROP TABLE IF EXISTS "CreditTransactions";
DROP TABLE IF EXISTS "CreditWallets";
DROP TABLE IF EXISTS "Reveals";
DROP TABLE IF EXISTS "MatchConfirmations";
DROP TABLE IF EXISTS "Matches";

-- Revert application code to previous version
git checkout HEAD~1 -- ./

-- Restart service
```

---

## Deployment Checklist

- [ ] Database migration applied successfully
- [ ] Wallets initialized for existing users
- [ ] Application builds without errors
- [ ] Test data created in staging
- [ ] All 5 test scenarios pass (confirm, reveal, idempotency, insufficient credits, invalid state)
- [ ] Performance baseline met
- [ ] Mobile app updated for new endpoints
- [ ] Documentation communicated to support team
- [ ] Monitoring & alerts configured
- [ ] Deployment to production
- [ ] Post-deployment smoke tests pass

---

## Support & Questions

For issues or questions:
1. Check troubleshooting section
2. Review audit queries to verify data state
3. Check application logs for detailed error messages
4. Review UNLOCK_REFACTOR_SUMMARY.md for architecture details

---

**Date:** 2026-08-22  
**Version:** 1.0  
**Status:** Ready for Testing & Deployment
