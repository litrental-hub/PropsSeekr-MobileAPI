# PropSeekr API application context

Last verified against the `Main` branch on 2026-08-28.

This document is the backend source of truth for future feature work. Update it whenever a change alters a business rule, API contract, database source, state transition, external integration, or deployment requirement. Never add credentials, private keys, access tokens, connection strings, or customer data here.

## Product purpose

PropSeekr is a broker-to-broker Indian real-estate marketplace. A broker can publish property supply as a listing, publish client demand as a requirement, receive compatible matches, ask the counterparty broker to connect, and reveal contact details after mutual consent. Tokens fund successful contact reveals; creating or merely viewing a match does not spend tokens.

The core loop is:

```text
Broker account -> broker identity -> listing or requirement
       -> Gemini embedding -> deterministic matching procedure
       -> match -> two-broker confirmation -> atomic reveal and token charge
```

## Runtime architecture

- ASP.NET Core Web API targeting .NET 10 (`PropSeekr.csproj`).
- Entity Framework Core 9 with Npgsql and NetTopologySuite.
- PostgreSQL with PostGIS-style geography data, `pgvector`, and `pg_trgm` support.
- JWT bearer authentication with `User.Role` supplying the `Admin` or `User` role claim.
- Google Vertex AI `gemini-embedding-001` for 1,536-dimensional embeddings by default.
- A vendored file processor that runs behind ASP.NET endpoints and retains Lambda/S3-compatible request shapes.
- AWS Secrets Manager, S3, SES, ECS/ECR, and Razorpay integrations.
- Local HTTP profile: `http://localhost:5150`; container port: `8080`.
- Database migrations do not run at startup unless `Database:ApplyMigrationsOnStartup` is explicitly enabled.

`Program.cs` is the composition root. It loads optional Secrets Manager configuration, bridges `FileProcessor:*` settings into the environment names expected by the processor, configures the database, registers services, configures authentication/authorization, and exposes controllers plus `/hello`.

## Identity and authorization

`Users` is the authentication source. A user has a persisted `Role` and may link to one numeric legacy/canonical `BrokerId`.

- Login accepts username, mobile number, or email through `POST /api/v1/auth/login`.
- Registration creates a `User`, creates or links a `Broker`, and initializes a wallet with ten free credits when needed.
- JWTs contain the user GUID as `NameIdentifier` and a normalized `Admin` or `User` role claim.
- `BrokerIdentityService` is the bridge from a user GUID to broker-owned data. It first uses `User.BrokerId`, then falls back to the final ten digits of the mobile number and persists the link.
- Broker-scoped actions must derive the broker ID from the authenticated user. Do not trust a client-supplied broker ID for authenticated create, match, wallet, or reveal operations.
- Admin list endpoints intentionally remove broker ownership scope. Currently this applies to `/listings/mine`, `/requirements/mine`, `/user-matches`, and the admin search projection.

The custom `Authentication/JwtAuthenticationHandler.cs` is not registered by `Program.cs`; the active implementation is ASP.NET's standard JWT bearer handler. Do not base new behavior on the custom handler unless registration is deliberately changed and tested.

## Canonical data model

The main domain graph is:

```text
User (GUID account, role)
  -> Broker (integer domain identity)
       -> Listing
       -> Requirement
       -> CreditWallet + CreditTransactions
       -> BrokerNotifications

Listing + Requirement
  -> Match
       -> MatchConnectionRequest
       -> MatchConfirmation (one per broker)
       -> Reveal (at most one per match)
```

Important sources of truth:

- `listings_table` and `requirements_table` are the underlying ingestion/matching tables in the deployed legacy schema.
- `listings` and `requirements` are the EF-facing active views/tables used by much of the API. Existing migrations create at least the `requirements` active view over `requirements_table`; deployments must confirm the corresponding database objects before changing mappings.
- `matches` is the canonical listing-to-requirement result table. `matchid` is the unlock identity.
- `credit_wallets` and `credit_transactions` are canonical for current token flows.
- `reveals` is the authority for whether contact information can be returned. Match state alone is not sufficient.
- `match_connection_requests` records request direction and outcome.
- `match_confirmations` records each broker's checklist and four-hour expiry.
- `notifications` mapped as `BrokerNotification` is the canonical broker/matching notification stream used by the mobile UI.

There are legacy parallel models that must not be mixed into new matching work:

- `PropertyRequests` is an older combined supply/demand model. Non-admin search still queries it, but canonical inventory and matching use `Listing` and `Requirement`.
- `Notification` is a GUID user-notification model with an older unlock path; `BrokerNotification` is the numeric broker notification model used by the current match handshake.
- `User.Credits` and `UnlockedProperty` belong to the legacy credit/unlock flow; the current match reveal uses `CreditWallet`, `CreditTransaction`, and `Reveal`.

New work should extend the canonical broker/listing/requirement/match/wallet graph unless it is explicitly a migration of legacy data.

## Listing and requirement creation

### Listing

`POST /api/v1/listings`:

1. Resolves the authenticated user's broker ID and overwrites the request broker ID.
2. Normalizes transaction values to `RENT`, `SELL`, or `LEASE`.
3. Creates the listing and optional size/link rows in a database transaction.
4. Commits the listing before invoking the matching pipeline.
5. Synchronously waits for targeted embedding and matching.
6. Returns `embedding_completed` and a match count. A pipeline failure does not roll back the already-created listing.

`POST /api/v1/listings/whatsapp-intake` is anonymous for processor/Lambda compatibility and accepts a broker ID. It must be protected by an internal network or API gateway policy before public deployment.

### Requirement

`POST /api/v1/requirements`:

1. Validates budget, minimum size, city, locality, GPS coordinates, radius, and property type.
2. Resolves the broker from the authenticated user.
3. Normalizes `RENTAL` to `RENT` and buy variants to `BUY`.
4. Creates the canonical requirement.
5. Synchronously invokes targeted embedding and matching.
6. Returns `embeddingCompleted` and `matchCount`; a pipeline failure leaves the requirement saved.

Current gap: the requirement service validates locality, latitude, longitude, and radius, but the canonical `Requirement` entity does not persist those submitted fields. It stores city and embeds locality in `RawMessageText`; `PreferredLocalityIds` remains unset unless another ingestion path supplies it. Do not assume radius matching is active for manually created requirements.

Current gap: the listing UI obtains GPS coordinates, but its listing payload does not send them and the current listing DTO/model has no direct latitude/longitude fields. Locality resolution relies on `MasterId`, city, project/locality text, or ingestion.

## Embedding pipeline

The live create path is:

```text
ListingsController or RequirementService
  -> MatchingPipelineService
  -> FileProcessorHost (lazy reusable processor)
  -> processor /embed route
  -> VertexAiEmbeddingClient
  -> write embedding + embedding_model
  -> CALL sp_run_matching_engine(target requirement/listing)
```

Key behavior:

- The create endpoint awaits this pipeline. The wording "started" is no longer sufficient; callers receive a completion flag.
- Only rows with `embedding IS NULL`, non-empty `raw_message_text`, and a non-deleted/non-closed status are selected.
- Embedding text combines property type, transaction/listing type, and at most the first 300 characters of raw text.
- Vertex AI uses a Google service account and the `RETRIEVAL_DOCUMENT` task type.
- `gemini-embedding-001` is the default model; output dimension defaults to 1,536 and is normalized before storage.
- The processor writes both `embedding` and `embedding_model`. Matching only applies vector score when both sides declare the same supported model.
- Batches are coordinated at the database level, but the Gemini client currently sends one prediction request per text. A failed batch falls back to individual processing.
- After embeddings are stored, the targeted stored procedure is called with either the new listing ID or requirement ID.

Required configuration names are mapped in `FileProcessing/FileProcessorConfigurationBridge.cs`. The important groups are database connectivity, AWS region/S3, Google service-account fields, `GOOGLE_CLOUD_PROJECT`, `GOOGLE_CLOUD_LOCATION`, `VERTEX_EMBEDDING_MODEL`, and `EMBEDDING_DIMENSIONS`. Values belong in environment variables, user secrets, or Secrets Manager—not source control.

When changing model or dimensions:

1. Update the database vector dimension and application configuration together.
2. Treat existing vectors as model-versioned data.
3. Plan an explicit re-embedding/backfill rather than silently comparing incompatible vectors.
4. Keep `embedding_model` populated and update the stored-procedure model guard.
5. Validate a known listing/requirement pair before rebuilding all automatic matches.

## Matching engine

The deployable matching definition is `scripts/harden-matching-engine.sql`; its database procedure is `public.sp_run_matching_engine(requirement_id, listing_id)`. `AutomatedMatchingService` and the file processor both invoke that procedure. Applying an EF migration does not automatically guarantee that the latest procedure body is installed; deployment must apply and verify the SQL in the intended database.

Hard candidate rules:

- Never match the same broker to itself.
- Both records must be active and available.
- Resolved cities are required and must match case-insensitively.
- Transaction directions must be compatible: buy demand with sale supply, rental with rental, lease with lease.
- Property type must be exact or belong to an explicit compatibility family.
- Configuration must match when the requirement supplies configurations.
- Locality must be exact, text-similar at least 0.60, or within 3 km when locality IDs exist.
- A fixed-budget requirement needs a comparable listing price and allows at most 10% headroom.
- Price and budget units are normalized across total, monthly, per-square-foot, per-bigha, and per-acre cases.

Score composition totals 100:

| Component | Maximum |
| --- | ---: |
| Location | 30 |
| Property type | 15 |
| Price/budget | 20 |
| Size | 10 |
| Configuration | 10 |
| Furnishing | 5 |
| Vector similarity | 10 |

Candidates below 35 are excluded. The procedure retains at most 50 automatic matches per targeted listing/requirement scope. For a full rebuild, ranking is capped per requirement rather than globally.

Preservation rule: the procedure deletes/rebuilds only rows whose status is still `MATCHED` in the requested scope. Confirmed, requested, revealed, or otherwise progressed matches must survive a re-run. Never replace this with a broad delete.

The database tier labels are currently 80+ `TIER1`, 60+ `TIER2`, otherwise `TIER3`. `UserMatchesService` aggregate buckets currently use 90+ excellent, 75–89 good, and below 75 fair. The mobile card labels currently use 80/60. This is a known semantic inconsistency; choose one product definition and change database, API, tests, and UI together.

## Mutual connection and contact reveal

`UnlockService` is the canonical implementation. The mobile application's normal flow must use:

```http
POST /api/v1/user-matches/matches/{matchId}/confirm
POST /api/v1/user-matches/matches/{matchId}/reject
```

The required sequence is:

1. Broker A confirms availability, price/budget validity, negotiability, and readiness.
2. Broker A must already have at least one token, but no token is deducted yet.
3. A pending connection request and Broker A confirmation are created for a four-hour window.
4. A registered Broker B receives an in-app notification. For an unregistered broker, WhatsApp delivery is marked `planned`; it is not currently sent.
5. Broker B accepts through the same confirm endpoint or rejects with a structured reason.
6. On acceptance, both valid confirmations and both wallets are checked.
7. One transaction atomically creates the reveal, deducts one token from each wallet, creates both ledger entries, accepts the connection request, updates match state, and creates the outcome notification.
8. Contact data is returned only when a `reveals` row exists.

Safety properties:

- A row lock on the match plus the unique reveal constraint makes retries/concurrency idempotent.
- Both brokers are charged exactly once or neither broker is charged.
- Insufficient credit sets the request to `credit_required`; it must not create a reveal or partial ledger entries.
- Only a broker who is a party to the match can confirm/reveal.
- Only the receiving broker can accept or reject a pending request.
- Rejection resets confirmations, reveals nothing, and deducts nothing.
- Expiration reveals nothing and deducts nothing.

Legacy/direct reveal endpoints still exist. They are not authorization to bypass mutual confirmation in the mobile experience. If a direct reveal endpoint is retained, treat it as an internal/idempotent completion or compatibility surface and protect it accordingly.

## Current API surface

All routes are under `/api/v1` unless stated otherwise.

| Area | Current routes used or supported |
| --- | --- |
| Authentication | `POST /auth/register`, `/auth/login`, email/mobile OTP send and verify, resend, logout |
| Listings | `GET /listings/mine`, `POST /listings`, `GET /listings/{id}`, `GET /listings`, `PATCH /listings/{id}`, anonymous `/listings/whatsapp-intake` |
| Requirements | `GET /requirements/mine`, `POST /requirements` |
| Search | `POST /search/properties` |
| Matches | `GET /user-matches`, confirm, reject, reveal compatibility action, unlock compatibility action, unlocked history |
| Broker data | broker register/get/update, matches, wallet, ledger, notifications, notification preferences |
| Wallet/payment | credit packs, Razorpay order/verify/webhook, alternate `/payments` flow, internal monthly grant/deduct |
| File processor | process, embed, ingest, matches, listing, presigned upload, full pipeline callback |
| Operations | matching run/expiry check, `/hello`, Swagger/OpenAPI |

Important contract gap: the mobile Axios interceptor calls `POST /auth/refresh`, but the current `AuthController` exposes no refresh endpoint and the API does not persist refresh tokens. Expired access tokens therefore lead to logout. Implement refresh end-to-end before relying on it.

## Mobile-facing response rules

- Inventory endpoints are paginated. `totalCount`/metadata is the aggregate; `data.length` is only the loaded page.
- For admin users, `mine` intentionally means all brokers' records, still constrained by transaction/status filters and pagination.
- Preserve listing-versus-requirement source IDs. A requirement ID must never be sent as `listingId`.
- `UserMatchesService` only projects counterparty contact fields after a reveal.
- The canonical match response includes state, current-broker confirmation, expiry, reveal state, connection request status/direction, broker role, both property/requirement summaries, and aggregate quality counts.

## External integrations and operational boundaries

- Google Maps SDK configuration in the mobile client is separate from the Vertex AI service account used by the API.
- AWS credentials should come from workload roles/OIDC and Secrets Manager. Static AWS keys must not be committed.
- Razorpay order verification and webhook handling must remain idempotent; successful payment credits the canonical wallet and ledger once.
- File-processor endpoints and internal matching/credit operations are currently anonymous for infrastructure compatibility. They require network/API-gateway protection before internet exposure.
- CORS currently allows every origin and Swagger is enabled globally. Tighten these for production as a coordinated deployment change.
- The current health endpoint is liveness-only (`/hello`). A database-aware readiness endpoint is still needed.

## Known architectural seams

These are current facts, not instructions to silently repair unrelated work:

1. Canonical and legacy inventory/search/notification/credit paths coexist.
2. Non-admin search uses legacy `PropertyRequests`, while admin search reads canonical listings and requirements.
3. Manual locality/GPS values are not fully persisted into the canonical matching locality model.
4. Match quality thresholds differ between SQL tiers, API aggregates, and UI labels.
5. The UI expects a refresh-token endpoint that does not exist.
6. Multiple confirm/reveal and payment controllers expose overlapping compatibility surfaces.
7. File-processor public endpoints and internal cron endpoints need infrastructure-level protection.
8. App configuration and `.env.example` use both `DB_USERNAME` and the older `DB_USER` spelling; runtime code expects `DB_USERNAME`.

When fixing a seam, migrate callers and tests deliberately. Do not make a second parallel source of truth.

## Change checklist

Before implementing a feature:

1. Identify whether it belongs to the canonical or legacy domain.
2. Trace authenticated user GUID to broker ID and confirm admin behavior.
3. Check the mobile DTO and endpoint currently used, not only an unused API helper.
4. For listing/requirement changes, evaluate embedding text, vector backfill, and procedure compatibility.
5. For matching changes, preserve hard filters and progressed matches and test false-positive examples.
6. For contact or credit changes, preserve mutual consent, atomic two-wallet deduction, idempotency, and reveal-gated contact projection.
7. Treat schema/procedure deployment separately from compiling EF code.
8. Add or update focused tests and update this document when the contract changes.

## Build and verification

```bash
dotnet build --no-restore
dotnet test Tests/PropsSeekr-MobileAPI.Tests/PropsSeekr-MobileAPI.Tests.csproj --no-restore
dotnet run --no-build --launch-profile http
```

Relevant test coverage includes admin scoping, broker identity, listing/requirement inventory, source-specific match filters, dual confirmation, concurrent reveal idempotency, insufficient credit, payment wallet idempotency, and matching normalization helpers. PostgreSQL integration tests require their configured test database and may be skipped when unavailable.

## Code map

- `Program.cs`: runtime composition and configuration.
- `Controllers/`: HTTP contracts and role checks.
- `Services/`: application logic; `UnlockService`, `UserMatchesService`, `RequirementService`, and `BrokerListingsService` are especially business-sensitive.
- `Models/` and `Data/AppDbContext.cs`: EF-facing data model.
- `FileProcessor/`: ingestion, extraction, Vertex embeddings, and processor-compatible APIs.
- `FileProcessing/`: ASP.NET host/configuration adapters for the vendored processor.
- `scripts/harden-matching-engine.sql`: current precision-first stored procedure.
- `scripts/matching-engine-schema.sql`: required vector/trigram/helper schema.
- `Migrations/`: EF schema history; do not assume it alone installs the latest stored procedure.
- `Tests/PropsSeekr-MobileAPI.Tests/`: unit and PostgreSQL integration protection.
