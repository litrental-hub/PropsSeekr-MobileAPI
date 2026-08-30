# PropSeekr API application context

Last verified against the `Main` branch on 2026-08-29.

This document is the backend source of truth for future feature work. Update it whenever a change alters a business rule, API contract, database source, state transition, external integration, or deployment requirement. Never add credentials, private keys, access tokens, connection strings, or customer data here.

`DATABASE_SCHEMA_CONTEXT.md` is the companion authority for canonical tables,
relationships, indexes, legacy database differences, and schema-audit checks.

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
- Internal service endpoints (file processor, matching run/expiration, monthly credit grant, credit deduction, and WhatsApp intake) require an `X-Internal-Service-Key` header matching `InternalService:ApiKey` or `INTERNAL_SERVICE_API_KEY`.

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

Listing
  -> ListingDetail (structured form metadata)
  -> ListingMedia (authorized photo/video metadata)
```

Important sources of truth:

- `listings` and `requirements` are the canonical EF-facing tables and stored procedure targets used across the API, matching engine (`sp_run_matching_engine`), and marketplace search (`SearchPropertyService.cs` / `POST /api/v1/search/properties`). All marketplace search queries for normal users and admins query canonical `listings` and `requirements`. Legacy `PropertyRequests` routes (`PropertyInventoryController.cs`) are retired with 410 Gone.
- `matches` is the canonical listing-to-requirement result table. `matchid` is the unlock identity.
- `credit_wallets` and `credit_transactions` are canonical for current token flows.
- `reveals` is the authority for whether contact information can be returned. Match state alone is not sufficient.
- `listing_details` stores property-type-specific form fields as bounded JSONB plus the owner's photo-sharing preference. `listing_media` stores media metadata and a server-managed relative storage path; neither table participates in matching.
- `match_connection_requests` records request direction and outcome.
- `match_confirmations` records each broker's checklist and four-hour expiry.
- `notifications` mapped as `BrokerNotification` is the canonical broker/matching notification stream used by the mobile UI.

There are legacy parallel models that must not be mixed into new matching work:

- `PropertyRequests` is an older combined supply/demand model. Marketplace search no longer queries it; canonical inventory and matching use `Listing` and `Requirement`.
- `Notification` is a GUID user-notification model with an older unlock path; `BrokerNotification` is the numeric broker notification model used by the current match handshake.
- `User.Credits` and `UnlockedProperty` belong to the legacy credit/unlock flow; the current match reveal uses `CreditWallet`, `CreditTransaction`, and `Reveal`.

New work should extend the canonical broker/listing/requirement/match/wallet graph unless it is explicitly a migration of legacy data.

## Listing and requirement creation

### Listing

`POST /api/v1/listings`:

1. Resolves the authenticated user's broker ID and overwrites the request broker ID.
2. Normalizes transaction values to `RENT`, `SELL`, or `LEASE`.
3. Creates the listing and optional size/link rows in a database transaction.
4. Commits the listing transaction before calling the matching pipeline.
5. Currently waits synchronously for targeted embedding and matching, then returns `embedding_completed` and `match_count`.
6. A pipeline failure does not roll back the already-created listing; the response reports `embedding_completed: false`.

Manual listing creation can also persist a JSON object in `details` (maximum 32 KB) and `photo_sharing_preference`. Authenticated listing owners upload up to 12 JPG/PNG/WEBP/MP4/MOV/WEBM files through `POST /api/v1/listings/{listingId}/media`. Images are limited to 10 MB and videos to 100 MB by default. Media bytes currently live below the API web root, while database rows store relative paths; production with ephemeral or horizontally scaled API instances must move bytes to durable shared/object storage without changing reveal rules.

Migration `20260828000100_AddListingDetailsAndMedia` creates the two additive tables. Migration `20260828000200_AddRequirementMatchingPreferences` adds requirement range/radius/project columns and updates the active requirements view. Both must be applied explicitly in each target database because startup migrations remain disabled by default. Apply and verify `scripts/harden-matching-engine.sql` after the migrations; compiling the API does not install the procedure.

`POST /api/v1/listings/whatsapp-intake` is anonymous for processor/Lambda compatibility and accepts a broker ID. It must be protected by an internal network or API gateway policy before public deployment.

### Requirement

`POST /api/v1/requirements`:

1. Validates fixed/flexible budget semantics, minimum/maximum size, up to five same-city preferred localities, GPS coordinates, radius from 0-100 km, optional project names, and property type.
2. Resolves the broker from the authenticated user.
3. Normalizes `RENTAL` to `RENT` and buy variants to `BUY`.
4. Creates the canonical requirement with optional `budget_min`, `size_max`, `radius_km`, and `preferred_project_names`. Historical callers remain compatible: fixed budget is the default, one legacy locality is accepted, and a missing stored radius matches at 3 km.
5. Currently invokes targeted embedding and matching synchronously after its transaction commits.
6. Returns `embeddingCompleted` and `matchCount`; a pipeline failure leaves the requirement saved and reports `embeddingCompleted: false`.

Manual listing and requirement creation resolve the submitted/geocoded city, locality, latitude, and longitude into the canonical `master` catalogue in the same database transaction as inventory creation. Listings persist the resulting `MasterId`; requirements persist one to five resolved IDs in `PreferredLocalityIds`. Existing catalogue coordinates are retained, and missing catalogue rows are created under a transaction-level advisory lock.

Canonical master rows also persist location provenance: `geocoding_status`, provider/place ID, formatted address, precision, confidence, timestamp, error, and review flag. Coordinates selected in the manual Google Maps flow are stored as `verified`/`user`; server-geocoded imports are stored as `resolved`/`google`. Listings and requirements carry their own resolution status, note, and timestamp. Only `resolved` or `verified` canonical locations may participate in nearby search or automatic matching.

New manual inventory passes through one normalization layer before persistence. Property-type aliases, BHK spacing, furnishing aliases such as `BARE`/`UNFURNISHED`, and facing abbreviations are stored using canonical values. The procedure applies equivalent normalization at read time so historical records do not require an immediate destructive backfill. Listing creation now persists the existing canonical `floor_number`, `road_info`, `price_status`, and `project_name` fields when supplied by the mobile form.

The mobile listing and requirement forms geocode the property/preferred locality text. They must not attach the broker's current GPS position to an inventory record unless that position is explicitly the property/preferred location.

## Embedding pipeline

## Bulk TXT import pipeline

Mobile bulk uploads use the authenticated `POST /api/v1/bulk-imports/uploads` endpoint, including `defaultCity`, upload the returned presigned URL directly to S3, then call `POST /api/v1/bulk-imports/{jobId}/complete`. The UI initializes the fallback from the user's selected city and uses `Indore` when none exists or the field is blank. This fallback is applied only when an extracted record has no explicit city; an explicitly named city always wins. The API records the fallback on the broker-owned `bulk_import_jobs` row before issuing the URL. `BulkImportJobWorker` parses the text file, ingests canonical listings/requirements, resolves locations with Google server-side Geocoding, embeds both targets, and runs matching asynchronously. Job status, fallback city, and counts are available through `GET /api/v1/bulk-imports/{jobId}`; failed jobs can be requeued through `POST /api/v1/bulk-imports/{jobId}/retry`.

Server geocoding uses a backend-only Google key from `FileProcessor:GoogleMapsApiKey`, `GOOGLE_MAPS_API_KEY`, or Secrets Manager. It is separate from the Android Maps SDK key, must be restricted to the Geocoding API and production server egress IPs, and must never be committed. New provider results are automatically accepted only when the expected city matches and the confidence score is at least 0.70; all other results retain no coordinates and are marked `review_required`. Canonical name similarity in import resolution is at least 0.75, and alias matching is exact by token rather than substring.

Historical remediation is managed through admin-only `POST /api/v1/location-remediation/jobs` and `GET /api/v1/location-remediation/jobs/{id}`. `LocationRemediationWorker` is cursor-based and resumable. It geocodes missing master coordinates in bounded batches, links inventory only when source text has one unambiguous trusted locality, routes all other rows to review, and invokes the matching procedure only for each repaired record. It never performs an implicit global match rebuild.

Claimed bulk jobs have a unique `lock_token` and refresh `locked_at` every two
minutes. Completion and retry updates require the same token, preventing a
healthy import that runs longer than the 30-minute stale-job threshold from
being reclaimed by another API instance.

The legacy `/file-processor/*` facade remains internal-service-only. Mobile clients must never send the internal service key and must use `/bulk-imports` instead.

The asynchronous job path is:

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

- `embedding_jobs`, `EmbeddingJobWorker`, `GET /api/v1/embedding-jobs/{jobId}`, and the owner-authorized retry route exist, but the current manual listing/requirement create and update handlers have not yet been wired to enqueue them. They are therefore not the source of truth for those UI submissions today.
- The partial unique database index allows at most one queued job for a listing or requirement once the manual handlers are wired to enqueue. A PostgreSQL advisory lock protects enqueue/retry decisions across API instances.
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
- Resolved cities are required and must match case-insensitively. Listing and configured requirement localities must have canonical `resolved` or `verified` geocoding status.
- Transaction directions must be compatible: buy demand with sale supply, rental with rental, lease with lease.
- Property type is normalized across historical aliases, then must be exact or belong to an explicit compatibility family.
- Configuration must match when the requirement supplies configurations.
- Locality must be exact, text-similar at least 0.60, or within the requirement's stored radius when locality IDs exist. Historical rows without a radius default to 3 km.
- A fixed-budget requirement needs a comparable listing price and allows at most 10% headroom.
- Price and budget units are normalized across total, monthly, per-square-foot, per-bigha, and per-acre cases.

Score composition totals 100:

| Component | Maximum |
| --- | ---: |
| Location | 25 |
| Property type | 15 |
| Price/budget | 20 |
| Size | 10 |
| Configuration | 10 |
| Furnishing | 5 |
| Facing | 2 |
| Preferred project | 3 |
| Vector similarity | 10 |

Minimum and maximum size now use range semantics rather than symmetric closeness: a listing at or above a minimum is not penalized unless it exceeds an optional maximum. Fixed budgets retain the 10% hard ceiling; an optional minimum budget affects score rather than excluding a less-expensive property. Flexible budgets do not hard-reject on price but use a supplied preferred maximum for scoring. Facing and project preferences are soft scores, never hard filters.

Candidates below 35 are excluded. The procedure retains at most 50 automatic matches per targeted listing/requirement scope. For a full rebuild, ranking is capped per requirement rather than globally.

The `isavailable` flags are mapped in the EF listing/requirement entities. Create
and update handlers persist an explicitly supplied value, and marketplace search
excludes unavailable inventory in addition to the stored procedure doing so.

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

Notification presentation is resolved from both the stored notification type and the current linked connection-request status. Broker A receives a distinct `confirm_accepted` outcome when Broker B accepts. Broker B's original `confirm_pending` card also presents as accepted/handled after the request is accepted, rather than continuing to ask for acceptance. Both accepted cards deep-link to the exact revealed match.

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
| Listings | `GET /listings/mine`, `POST /listings`, `POST /listings/{id}/media`, `GET /listings/{id}`, `GET /listings`, `PATCH /listings/{id}`, anonymous `/listings/whatsapp-intake` |
| Requirements | `GET /requirements/mine`, `POST /requirements` |
| Search | `POST /search/properties` |
| Matches | `GET /user-matches`, `GET /user-matches/matches/{id}/details`, authenticated match media, confirm, reject, reveal compatibility action, unlock compatibility action, unlocked history |
| Broker data | broker register/get/update, matches, wallet, ledger, notifications, notification preferences |
| Wallet/payment | credit packs, Razorpay order/verify/webhook, alternate `/payments` flow, internal monthly grant/deduct |
| File processor | process, embed, ingest, matches, listing, presigned upload, full pipeline callback |
| Operations | matching run/expiry check, `/hello`, Swagger/OpenAPI |

Important contract gap: the mobile Axios interceptor calls `POST /auth/refresh`, but the current `AuthController` exposes no refresh endpoint and the API does not persist refresh tokens. Expired access tokens therefore lead to logout. Implement refresh end-to-end before relying on it.

## Mobile-facing response rules

- `/search/properties` is a canonical, authenticated marketplace query for every role. It requires `RENTAL` or `BUY_SELL`, `SUPPLY` or `DEMAND`, nested coordinates/radius, filters, and pagination.
- Search joins listings through `master_id` and requirements through `preferred_locality_ids`, applies an inline Haversine radius calculation, and returns distance-then-freshness ordering. Rows without canonical coordinates are excluded from nearby results.
- Rental supply maps to `RENT`/`RENTAL`, rental demand maps to the same values, buy/sell supply maps to sale values, and buy/sell demand maps to buy values.
- Search counts use the same transaction, radius, category, property type, configuration, budget, and text filters as result rows. The selected tab alone is paginated and returned.
- Search card fields are nullable and database-backed. The API must not invent area, availability, amenities, preferences, distance, dates, or unlock cost. Discovery responses deliberately exclude broker names, initials, brokerage details, phone numbers, other contact identity, and raw message text because ingestion messages may contain contact data. Home titles use structured property fields with neutral fallbacks. Home routes users to the source-filtered Matches screen; only the mutual confirmation/reveal flow may return contact details.
- Inventory endpoints are paginated. `totalCount`/metadata is the aggregate; `data.length` is only the loaded page.
- For admin users, `mine` intentionally means all brokers' records, still constrained by transaction/status filters and pagination.
- Preserve listing-versus-requirement source IDs. A requirement ID must never be sent as `listingId`.
- `UserMatchesService` only projects counterparty contact fields after a reveal.
- `GET /user-matches/matches/{matchId}/details` is the canonical match-detail projection. A normal caller must be the listing or requirement broker; an admin may inspect any match but does not receive counterparty contact through the admin projection. It returns both sides, all persisted canonical listing/requirement facts, structured listing details, and media metadata. Notes are scrubbed for Indian phone-number and email variants until a reveal exists.
- `GET /user-matches/matches/{matchId}/media/{mediaId}` streams one matched listing's media only to a match party or admin and supports range requests for video. Media URLs are authenticated API paths, not public static-file URLs.
- The canonical match response includes state, current-broker confirmation, expiry, reveal state, connection request status/direction, broker role, both property/requirement summaries, and aggregate quality counts.

## External integrations and operational boundaries

- Google Maps SDK configuration in the mobile client, the backend Geocoding API key, and the Vertex AI service account are separate credentials with separate restrictions.
- AWS credentials should come from workload roles/OIDC and Secrets Manager. Static AWS keys must not be committed.
- Razorpay order verification and webhook handling must remain idempotent; successful payment credits the canonical wallet and ledger once.
- File-processor endpoints and internal matching/credit operations are currently anonymous for infrastructure compatibility. They require network/API-gateway protection before internet exposure.
- CORS currently allows every origin and Swagger is enabled globally. Tighten these for production as a coordinated deployment change.
- The current health endpoint is liveness-only (`/hello`). A database-aware readiness endpoint is still needed.

## Known architectural seams

These are current facts, not instructions to silently repair unrelated work:

1. Canonical and legacy inventory/search/notification/credit paths coexist.
2. Historical canonical inventory without `master_id`/`preferred_locality_ids`, or whose master rows have no coordinates, cannot participate in nearby search until it is backfilled.
3. Match quality thresholds differ between SQL tiers, API aggregates, and UI labels.
4. The UI expects a refresh-token endpoint that does not exist.
5. Multiple confirm/reveal and payment controllers expose overlapping compatibility surfaces.
6. File-processor public endpoints and internal cron endpoints need infrastructure-level protection.
7. App configuration and `.env.example` use both `DB_USERNAME` and the older `DB_USER` spelling; runtime code expects `DB_USERNAME`.

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

`PROPSEEKR_RUN_LIVE_SEARCH_SMOKE=1` enables a read-only test of both canonical 5 km search projections against the database configured in `appsettings.json`. It does not create, update, or delete database rows.

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
