# PropSeekr Unlock Functionality Refactor - Complete Documentation

## 📋 Table of Contents
1. [Overview](#overview)
2. [What Changed](#what-changed)
3. [Architecture](#architecture)
4. [File Guide](#file-guide)
5. [API Reference](#api-reference)
6. [Database Schema](#database-schema)
7. [Quick Start](#quick-start)

---

## Overview

The unlock functionality has been completely redesigned to implement a robust dual handshake confirmation flow with credit wallet management. This ensures:

✅ **Fair Exchange** - Both brokers must confirm before contacts are revealed  
✅ **Monetization** - Credits are tracked via immutable ledger  
✅ **Idempotency** - Safe to retry operations without double-charging  
✅ **Auditability** - Full transaction history for compliance  
✅ **Security** - Contact details only exposed after confirmation & payment  

---

## What Changed

### Breaking Changes

| Component | Before | After | Migration |
|-----------|--------|-------|-----------|
| Unlock ID | `propertyRequestId` (Guid) | `matchId` (Guid) | Use `Match.Id` instead |
| Unlock Flow | Single step (direct unlock) | Dual step (confirm → reveal) | Call confirm first, then reveal |
| Contact Owner | Property owner | Match counterparty | Derived from match direction |
| Credit Tracking | `User.Credits` field | `CreditWallet` + `CreditTransaction` ledger | Use wallet balance queries |
| API Endpoint | `POST /unlock` | `POST /matches/{id}/confirm` + `POST /matches/{id}/reveal` | Update mobile app |

### New Features

- Dual handshake pre-reveal confirmation
- Credit wallet with free + paid credit balances
- Immutable transaction ledger for auditing
- Credit packs for monetization
- Payment gateway integration support
- Reveal idempotency

### Backward Compatibility

- Old `POST /unlock` endpoint still works (marked `[Obsolete]`)
- Uses `matchId` instead of `propertyRequestId`
- Existing `UnlockedProperty` records not affected
- `User.Credits` field remains for compatibility

---

## Architecture

### Data Flow Diagram

```
User A (Listing Broker)              User B (Requirement Broker)
        |                                    |
        |--- PropertyRequest A               |
        |                                    |
        |---POST /confirm ---------> Match   |
        |                           <------- POST /confirm
        |      Both confirmed, Match.State = 'confirmed'
        |
        |---POST /reveal ---------> Creates Reveal record
        |                           Deducts 1 credit
        |                           Returns Contact B ----+
        |                                                   |
        |<---------- Contact B <----- CreditTransaction ----+

Database:
- Match: Links listing to requirement with score & state
- MatchConfirmation: Broker checklist before unlock (2 rows per match)
- Reveal: Confirmation that contacts were exposed (1 row per match, immutable)
- CreditWallet: User's current balance (free + paid)
- CreditTransaction: Immutable ledger of all credit operations
```

### State Machine: Match Lifecycle

```
                 ┌─────────────────┐
                 │   MATCHED       │
                 │  (initial)      │
                 └────────┬────────┘
                          │
                   Both brokers confirm
                   (POST /confirm x2)
                          │
                 ┌────────▼──────────┐
                 │ PENDING_           │
                 │ CONFIRMATION      │
                 └────────┬──────────┘
                          │
                   Both ConfirmedAt set
                          │
                 ┌────────▼──────────┐
                 │   CONFIRMED       │
                 │  (ready for       │
                 │   reveal)         │
                 └────────┬──────────┘
                          │
                   POST /reveal (optional)
                   OR WindowExpiresAt passed
                          │
         ┌────────────────┴───────────────┐
         │                                │
    ┌────▼────┐              ┌────────────▼──┐
    │ EXPIRED  │              │ CONFIRMED     │
    │ (window  │              │ (contacts     │
    │ closed)  │              │  revealed)    │
    └──────────┘              └───────────────┘
```

### Component Diagram

```
┌──────────────────────────────────────────────────────────┐
│                   UserMatchesController                   │
│  GET /user-matches    POST /unlock (legacy, [Obsolete])  │
│  GET /unlocked                                            │
└──────────┬───────────────────────────────────────────────┘
           │
┌──────────▼───────────────────────────────────────────────┐
│            New: Unlock Flow Controller                    │
│  POST /matches/{id}/confirm                              │
│  POST /matches/{id}/reveal                               │
└──────────┬───────────────────────────────────────────────┘
           │
┌──────────▼───────────────────────────────────────────────┐
│  UserMatchesService (legacy)                             │
│  ├─ GetUserMatchesAsync()                                │
│  ├─ UnlockPropertyAsync() [updated to use MatchId]       │
│  └─ GetUnlockedPropertiesAsync()                         │
│                                                          │
│  UnlockService (NEW)                                     │
│  ├─ ConfirmMatchAsync()                                  │
│  ├─ UnlockMatchAsync()                                   │
│  ├─ IsMatchRevealedAsync()                               │
│  ├─ GetWalletAsync()                                     │
│  └─ InitializeWalletAsync()                              │
└──────────┬───────────────────────────────────────────────┘
           │
┌──────────▼───────────────────────────────────────────────┐
│              AppDbContext                                │
│                                                          │
│  New DbSets:                                             │
│  ├─ Matches                                              │
│  ├─ MatchConfirmations                                   │
│  ├─ Reveals                                              │
│  ├─ CreditWallets                                        │
│  ├─ CreditTransactions                                   │
│  ├─ CreditPacks                                          │
│  └─ Payments                                             │
└──────────┬───────────────────────────────────────────────┘
           │
┌──────────▼───────────────────────────────────────────────┐
│         PostgreSQL Database (AWS RDS)                    │
│                                                          │
│  Tables:                                                │
│  ├─ Matches (with state FSM)                            │
│  ├─ MatchConfirmations (dual handshake)                 │
│  ├─ Reveals (immutable unlock record)                   │
│  ├─ CreditWallets (current balance snapshot)             │
│  ├─ CreditTransactions (immutable ledger)                │
│  ├─ CreditPacks (catalog)                               │
│  └─ Payments (payment history)                           │
└──────────────────────────────────────────────────────────┘
```

---

## File Guide

### 📁 Models Directory
```
Models/
├── Match.cs                    NEW - Core match entity
├── MatchConfirmation.cs        NEW - Pre-reveal checklist
├── Reveal.cs                   NEW - Immutable unlock record
├── CreditWallet.cs             NEW - User balance tracking
├── CreditTransaction.cs        NEW - Immutable ledger
├── CreditPack.cs               NEW - Credit packages
├── Payment.cs                  NEW - Payment transactions
├── UnlockedProperty.cs         UNCHANGED - Legacy compatibility
├── PropertyRequest.cs          UNCHANGED
├── User.cs                     UNCHANGED (Credits field still there)
└── Notification.cs             UNCHANGED
```

### 📁 Services Directory
```
Services/
├── UnlockService.cs            NEW - Core unlock business logic
│   ├─ ConfirmMatchAsync()       Step 1: Dual handshake
│   ├─ UnlockMatchAsync()        Step 2: Reveal & deduct
│   ├─ IsMatchRevealedAsync()    Status check
│   ├─ GetWalletAsync()          Retrieve balance
│   └─ InitializeWalletAsync()   Setup new wallet
│
├── Interfaces/
│   └── IUnlockService.cs        NEW - Contract interface
│
├── UserMatchesService.cs        UPDATED
│   └─ UnlockPropertyAsync() [now uses MatchId]
│
└── [other services unchanged]
```

### 📁 Controllers Directory
```
Controllers/
├── UserMatchesController.cs     UPDATED
│   ├─ [HttpGet] GetUserMatches()         UNCHANGED
│   ├─ [HttpPost] ConfirmMatch()          NEW - /matches/{id}/confirm
│   ├─ [HttpPost] RevealMatch()           NEW - /matches/{id}/reveal
│   ├─ [HttpPost] UnlockProperty()        UPDATED [Obsolete] - uses MatchId
│   └─ [HttpGet] GetUnlockedProperties()  UNCHANGED
│
└── [other controllers unchanged]
```

### 📁 DTOs Directory
```
DTOs/Matches/
├── UnlockPropertyRequestDto.cs     UPDATED
│   ├─ BEFORE: public Guid PropertyRequestId
│   └─ AFTER:  public Guid MatchId
│
├── UnlockPropertyResponseDto.cs    UNCHANGED
│   └─ Response still same format
│
├── MatchConfirmationRequestDto.cs  NEW
│   └─ Pre-reveal checklist fields
│
├── MatchConfirmationResponseDto.cs NEW
│   └─ Confirmation status
│
├── UserMatchItemDto.cs             UPDATED
│   └─ Added: public Guid MatchId (for unlock operations)
│
└── [other DTOs unchanged]
```

### 📁 Data/Migrations Directory
```
Migrations/
├── 20260822_AddDualHandshakeAndCreditSystem.cs    NEW - Main migration
├── 20260822_Migration.sql                         NEW - SQL script
├── [previous migrations unchanged]
└── AppDbContextModelSnapshot.cs                   AUTO-GENERATED
```

### 📁 Configuration
```
Program.cs                        UPDATED
├─ Added: builder.Services.AddScoped<IUnlockService, UnlockService>();

Data/AppDbContext.cs              UPDATED
├─ Added 7 new DbSets (Match, MatchConfirmation, etc.)
├─ Added model configuration in OnModelCreating()
└─ Foreign key & index setup
```

### 📝 Documentation
```
UNLOCK_REFACTOR_SUMMARY.md         Complete technical reference
TESTING_AND_DEPLOYMENT.md          Step-by-step deployment guide
README.md (this file)              Architecture & quick start
Migrations/20260822_Migration.sql  Raw SQL migration script
run-migration.ps1                  PowerShell migration runner
```

---

## API Reference

### 1. Confirm Match (Dual Handshake Step 1)

**Endpoint:** `POST /api/v1/user-matches/matches/{matchId}/confirm`

**Authorization:** Bearer token required

**Path Parameters:**
- `matchId` (Guid): Match identifier

**Request Body:**
```json
{
  "matchId": "guid-here",
  "availabilityConfirmed": true,
  "priceValid": true,
  "priceNegotiable": false,
  "readyToConnect": true
}
```

**Response (200 OK) - First confirmation:**
```json
{
  "success": true,
  "message": "Confirmation recorded. Waiting for counterparty.",
  "matchId": "guid-here",
  "state": "pending_confirmation",
  "windowExpiresAt": "2026-08-23T14:30:00Z",
  "creditsRequired": 1
}
```

**Response (200 OK) - Both confirmed:**
```json
{
  "success": true,
  "message": "Match confirmed by both brokers. Ready to reveal contacts.",
  "matchId": "guid-here",
  "state": "confirmed",
  "windowExpiresAt": "2026-08-23T14:30:00Z",
  "creditsRequired": 1
}
```

**Response (404 Not Found):**
```json
{ "message": "Match not found." }
```

**Response (403 Forbidden):**
```json
{ "message": "User is not a party to this match." }
```

---

### 2. Reveal/Unlock Match (Dual Handshake Step 2)

**Endpoint:** `POST /api/v1/user-matches/matches/{matchId}/reveal`

**Authorization:** Bearer token required

**Path Parameters:**
- `matchId` (Guid): Match identifier

**Request Body:**
```json
{
  "matchId": "guid-here"
}
```

**Response (200 OK) - Success:**
```json
{
  "success": true,
  "message": "Contact details unlocked successfully!",
  "creditsRemaining": 4,
  "unlockedContact": {
    "ownerName": "Broker B Name",
    "ownerMobile": "9999999992",
    "ownerEmail": "broker.b@example.com"
  }
}
```

**Response (200 OK) - Already revealed (idempotent):**
```json
{
  "success": true,
  "message": "Contact details already unlocked.",
  "creditsRemaining": 4,
  "unlockedContact": {
    "ownerName": "Broker B Name",
    "ownerMobile": "9999999992",
    "ownerEmail": "broker.b@example.com"
  }
}
```

**Response (400 Bad Request) - Insufficient credits:**
```json
{
  "success": false,
  "message": "Insufficient credits. Required: 1, Available: 0",
  "creditsRemaining": 0
}
```

**Response (400 Bad Request) - Not confirmed:**
```json
{
  "success": false,
  "message": "Match state is 'matched', not 'confirmed'. Cannot reveal."
}
```

**Response (404 Not Found):**
```json
{ "message": "Match not found." }
```

---

### 3. Get Wallet (Query Credits)

**Endpoint:** `GET /api/v1/credits/wallet`

*Note: This endpoint needs to be added. Currently, use database query:*
```sql
SELECT "FreeCreditsBalance", "PaidCreditsBalance" 
FROM "CreditWallets" 
WHERE "UserId" = :userId;
```

---

### Legacy Endpoint (Deprecated)

**Endpoint:** `POST /api/v1/user-matches/unlock` [OBSOLETE]

⚠️ **Deprecated** - Use `/matches/{id}/confirm` and `/matches/{id}/reveal` instead

Still works for backward compatibility but will be removed in next major release.

**Request:**
```json
{ "matchId": "guid-here" }  // Changed from propertyRequestId
```

---

## Database Schema

### Match Table

```sql
CREATE TABLE "Matches" (
    "Id" uuid PRIMARY KEY,
    "ListingId" uuid NOT NULL,           -- FK: PropertyRequest (listing)
    "RequirementId" uuid NOT NULL,       -- FK: PropertyRequest (requirement)
    "MatchScore" numeric(5,2) NOT NULL,  -- 0-100 quality score
    "State" varchar(20) NOT NULL,        -- matched|pending_confirmation|confirmed|expired
    "CreatedAt" timestamp DEFAULT now(), 
    "UpdatedAt" timestamp DEFAULT now()
);

-- Indexes for fast queries
CREATE INDEX IX_Matches_State ON Matches("State");
CREATE INDEX IX_Matches_ListingId ON Matches("ListingId");
CREATE INDEX IX_Matches_RequirementId ON Matches("RequirementId");
```

### MatchConfirmation Table (Dual Handshake)

```sql
CREATE TABLE "MatchConfirmations" (
    "Id" uuid PRIMARY KEY,
    "MatchId" uuid NOT NULL,             -- FK: Matches
    "BrokerId" uuid NOT NULL,            -- FK: Users (broker confirming)
    "AvailabilityConfirmed" boolean,     -- Checklist fields
    "PriceValid" boolean,
    "PriceNegotiable" boolean,
    "ReadyToConnect" boolean,
    "ConfirmedAt" timestamp,             -- NULL until confirmed
    "WindowExpiresAt" timestamp,         -- 24-hour window
    "CreatedAt" timestamp DEFAULT now(),
    
    UNIQUE("MatchId", "BrokerId")        -- One confirmation per broker per match
);

-- Index for expiry CRON jobs
CREATE INDEX IX_MatchConfirmations_WindowExpiresAt 
    ON "MatchConfirmations"("WindowExpiresAt") 
    WHERE "ConfirmedAt" IS NULL;
```

### Reveal Table (Unlock Record)

```sql
CREATE TABLE "Reveals" (
    "Id" uuid PRIMARY KEY,
    "MatchId" uuid NOT NULL UNIQUE,      -- FK: Matches (one reveal per match)
    "RevealedAt" timestamp DEFAULT now()
);

-- Ensures idempotency (can't create 2 reveals for same match)
CREATE INDEX IX_Reveals_MatchId ON Reveals("MatchId");
```

### CreditWallet Table

```sql
CREATE TABLE "CreditWallets" (
    "Id" uuid PRIMARY KEY,
    "UserId" uuid NOT NULL UNIQUE,       -- FK: Users (one wallet per user)
    "FreeCreditsBalance" integer,        -- Expiring credits (reset monthly)
    "PaidCreditsBalance" integer,        -- Permanent paid credits
    "FreeCreditsResetAt" timestamp,      -- When free credits reset
    "CreatedAt" timestamp DEFAULT now(),
    "UpdatedAt" timestamp DEFAULT now()
);
```

### CreditTransaction Table (Immutable Ledger)

```sql
CREATE TABLE "CreditTransactions" (
    "Id" bigserial PRIMARY KEY,          -- Immutable ledger entry
    "UserId" uuid NOT NULL,              -- FK: Users
    "Type" varchar(20) NOT NULL,         -- grant|purchase|deduct|refund|expiry
    "Amount" integer NOT NULL,           -- Always positive
    "BalanceAfter" integer NOT NULL,     -- Snapshot of balance after transaction
    "ReferenceType" varchar(30),         -- reveal|payment|dispute|monthly_grant
    "ReferenceId" bigint,                -- ID in referenced table
    "Notes" text,
    "CreatedAt" timestamp DEFAULT now()  -- Immutable
);

-- Index for user ledger queries
CREATE INDEX IX_CreditTransactions_UserId_CreatedAt 
    ON "CreditTransactions"("UserId", "CreatedAt");
```

### CreditPack & Payment Tables

```sql
CREATE TABLE "CreditPacks" (
    "Id" uuid PRIMARY KEY,
    "Name" varchar(100) NOT NULL,
    "Credits" integer NOT NULL,
    "Price" decimal(10,2) NOT NULL,
    "Active" boolean DEFAULT true,
    "CreatedAt" timestamp DEFAULT now()
);

CREATE TABLE "Payments" (
    "Id" uuid PRIMARY KEY,
    "UserId" uuid NOT NULL,              -- FK: Users
    "CreditPackId" uuid,                 -- FK: CreditPacks (optional)
    "Amount" decimal(10,2) NOT NULL,
    "Currency" varchar(3) DEFAULT 'INR',
    "Gateway" varchar(50),               -- razorpay|stripe|etc
    "GatewayTransactionId" varchar(255) UNIQUE,  -- Idempotent webhook key
    "Status" varchar(20) DEFAULT 'initiated',    -- initiated|success|failed|refunded
    "CreatedAt" timestamp DEFAULT now(),
    "UpdatedAt" timestamp DEFAULT now()
);
```

---

## Quick Start

### For Developers

1. **Review Architecture:**
   ```bash
   cat UNLOCK_REFACTOR_SUMMARY.md
   ```

2. **Build & Test:**
   ```bash
   cd "c:\Users\Aman Jain\source\repos\PropsSeekr-MobileAPI"
   dotnet build
   dotnet run
   ```

3. **Run Integration Tests:**
   - Follow TESTING_AND_DEPLOYMENT.md Part 4

### For DevOps/Database Team

1. **Apply Migration:**
   ```bash
   # Option A: EF CLI
   dotnet ef database update
   
   # Option B: SQL Script
   psql -h host -U user -d dbname -f Migrations/20260822_Migration.sql
   ```

2. **Initialize Wallets:**
   ```sql
   -- See TESTING_AND_DEPLOYMENT.md Part 2
   ```

3. **Monitor:**
   ```sql
   -- See TESTING_AND_DEPLOYMENT.md Part 5
   ```

### For Mobile Developers

1. **Update API Calls:**
   - Old: `POST /api/v1/user-matches/unlock` with `propertyRequestId`
   - New: `POST /api/v1/user-matches/matches/{matchId}/confirm` then `/reveal`

2. **Update Match Display:**
   - Show `matchId` field from each match item
   - Pass `matchId` to unlock operations

3. **Update UI Flow:**
   - Add confirmation checklist dialog after user clicks "Unlock"
   - Then show "Waiting for other broker..." message
   - Finally call reveal when ready

---

## Key Design Decisions

### Why Dual Handshake?
Ensures both parties agree before credits are deducted. Prevents accidental unlocks.

### Why Immutable Ledger?
Complete audit trail. Credits can never be "lost" to bugs. Easier compliance & debugging.

### Why Separate Wallet from Transaction?
Wallet is quick snapshot for balance checks.  
Ledger is authoritative source for reconciliation.  
Prevents balance corruption from concurrent operations.

### Why State Machine?
Clear lifecycle prevents invalid operations. Example: Can't unlock a match that's not confirmed.

### Why Idempotent Reveal?
Safe to retry if network fails. Client can safely call `/reveal` multiple times.

---

## Migration Troubleshooting

**Q: Build fails with "does not contain definition for 'PropertyRequestId'"**
A: Update is incomplete. Check UserMatchesService.cs line numbers match fixes.

**Q: Database migration won't apply**
A: Try SQL script directly (Migrations/20260822_Migration.sql) instead of EF CLI.

**Q: Existing users have no wallet**
A: Run initialization SQL from TESTING_AND_DEPLOYMENT.md Part 2.

**Q: Tests fail with "Match not found"**
A: Verify test data inserted correctly (test MatchIds must exist).

---

**Last Updated:** 2026-08-22  
**Version:** 1.0  
**Status:** ✅ Ready for Deployment
