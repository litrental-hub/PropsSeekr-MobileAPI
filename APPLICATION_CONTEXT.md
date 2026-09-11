# PropSeekr API application context

Last reviewed on 2026-09-11, including the development database deployment checkpoint documented in `DATABASE_SCHEMA_CONTEXT.md`. This does not certify production deployment.

## 2026-09-11 audit and route cleanup

The development database now has the preservation-safe `scripts/harden-matching-engine.sql` body and the three 2026-09-11 additive migrations installed. The three previously overdue pending requests were closed through the canonical expiry transition without charges; the revealed row and all confirmation/request evidence remain linked. No broad matching rebuild was run. Four missing inventory embeddings are versioned queued jobs awaiting a correctly configured worker.

The original audit performed no writes and found all 26 FK checks clean, while 2,354 saved matches used the historical city-only requirement-locality path. The later authorized deployment is recorded above and in `DATABASE_SCHEMA_CONTEXT.md`. Freshness policy, historical wallet opening reconciliation, and evidence-based location remediation remain outstanding.

Removed unused HTTP surfaces after local caller review: duplicate `PaymentsController`, GUID `NotificationsController`, legacy `PropertyInventoryController`, manual `ListingRequirementsController`, duplicate `MatchesController`/`HandshakeController`, the retired auth refresh action, and retired user-match reveal/unlock/history actions. Removed unused mobile wrappers for deleted payment/manual-link routes. Removed routes now return 404 after deployment, not 410; canonical `/payment`, `/user-matches`, `/listings`, `/requirements`, and broker notifications remain. No schema/model/table was removed. Existing deployed clients outside the reviewed workspace still require release-log compatibility checks.

The current implementation branch closes the previously identified internal/webhook fail-open behavior, reveal retry/version checks, listing update invalidation, verified identity-update rules, monthly accounting, and private-media storage gaps. Its additive database migrations and matching SQL are installed in development; the API/mobile source changes, provider configuration, and production release remain undeployed.

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
- Background workers are disabled by default in Development so local Swagger/API runs do not process queue state in a shared database. Set `Workers:Enabled` to `true` when intentionally testing workers locally; non-Development environments enable them by default. Embedding jobs carry target versions, lease tokens, and heartbeats so stale workers cannot publish results for edited inventory.

`Program.cs` is the composition root. In every non-development server environment it requires and loads the JSON configuration secret named by `AWS:SecretsManagerConfigName`, using the ECS task role/default AWS credential chain. Database and JWT configuration are startup requirements. Missing internal-service or Razorpay configuration leaves those protected integrations unavailable and fail-closed instead of terminating the whole API; startup logs name unavailable configuration keys without printing their values. It bridges `FileProcessor:*` settings into the environment names expected by the processor, configures the database, registers services, configures authentication/authorization, and exposes controllers plus the backward-compatible `/hello` liveness endpoint and `/health/live`/`/health/ready`. The project does not use .NET Secret Manager or static AWS access keys.

## Identity and authorization

`Users` is the authentication source. A user has a persisted `Role` and may link to one numeric legacy/canonical `BrokerId`. `pending_registrations` is a separate, short-lived staging table and is never an authenticated identity.

- Login accepts username, mobile number, or email through `POST /api/v1/auth/login`.
- `POST /auth/register` validates uniqueness and stores only a 24-hour pending registration. It sends the email OTP and returns `pendingRegistrationId`, not a user identity.
- After email OTP verification, the client requests and verifies the mobile OTP. Only the successful mobile-OTP transaction creates the `User`, creates or claims the matching normalized-phone `Broker`, and initializes a wallet with ten free credits exactly once.
- A normal regular user must therefore have verified email, verified mobile, and a persisted `BrokerId`. Admin accounts remain the explicit exception because they do not own broker inventory.
- JWTs contain the user GUID as `NameIdentifier` and a normalized `Admin` or `User` role claim.
- `BrokerIdentityService` is the bridge from a user GUID to broker-owned data. It uses only the persisted `User.BrokerId`; broker claiming is permitted only after mobile verification.
- Broker-scoped actions must derive the broker ID from the authenticated user. Do not trust a client-supplied broker ID for authenticated create, match, wallet, or reveal operations.
- Admin list endpoints intentionally remove broker ownership scope. Currently this applies to `/listings/mine`, `/requirements/mine`, `/user-matches`, and the admin search projection.
- Internal service endpoints (file processor, matching run/expiration, monthly credit grant, credit deduction, and WhatsApp intake) require an `X-Internal-Service-Key` header matching `InternalService:ApiKey` or `INTERNAL_SERVICE_API_KEY`.

The custom `Authentication/JwtAuthenticationHandler.cs` is not registered by `Program.cs`; the active implementation is ASP.NET's standard JWT bearer handler. Do not base new behavior on the custom handler unless registration is deliberately changed and tested.

Pending registrations expire after 24 hours. They contain the same protected registration/KYC payload needed to finish account creation, including a password hash, but no JWT, role-bearing user row, broker link, or wallet. Do not use this table for login, authorization, or broker-scoped operations.

Registration recovery preserves the durable pending row when email delivery fails and returns a verification-required response with resend guidance. A repeat submission may resume only the same mobile/email/Aadhaar/PAN identity with its original password; it does not overwrite staged fields or extend expiry. Email proof must have been issued and verified after that registration began. A mobile code issued before the current pending registration cannot promote it.

Mobile OTP authentication rejects inactive accounts and regular accounts without verified email (except the existing Development-only bypass). Admin mobile authentication does not create a broker wallet. Email OTP returns a session only for active, fully verified accounts with a persisted broker link, or verified admins; it includes the persisted role and broker identity. Password-reset OTP does not return a session token.

The Secrets Manager contract now accepts only the explicitly mapped flat string keys documented in `scripts/DEPLOYMENT_SETUP.md`. `DB_CONNECTION_STRING` maps to `ConnectionStrings:DefaultConnection`; old nested secrets and separate database fields are rejected. Verify the deployed secret shape before rolling out this branch; a local build does not validate AWS configuration.

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
- `channel_status` tracks delivery (`pending` until an in-app poll returns it, then `delivered`); `read_at` alone tracks whether the broker has read it. A confirmation outcome is intentionally unread until the recipient marks it read.

There are legacy parallel models that must not be mixed into new matching work:

- `PropertyRequests` is an older combined supply/demand model. Marketplace search no longer queries it; canonical inventory and matching use `Listing` and `Requirement`.
- `Notification` is a GUID user-notification model with an older unlock path; `BrokerNotification` is the numeric broker notification model used by the current match handshake.
- `UnlockedProperty` belongs to the legacy credit/unlock flow; the current match reveal uses `CreditWallet`, `CreditTransaction`, and `Reveal`. Legacy `Users.Credits` and `brokers.credit_balance` were removed after wallet migration; `credit_wallets` is the sole balance store.

New work should extend the canonical broker/listing/requirement/match/wallet graph unless it is explicitly a migration of legacy data.

## Listing and requirement creation

### Listing

`POST /api/v1/listings`:

1. Resolves the authenticated user's broker ID and overwrites the request broker ID.
2. Normalizes transaction values to `RENT`, `SELL`, or `LEASE`.
3. Creates the listing and optional size/link rows in a database transaction.
4. Commits the listing transaction before calling the matching pipeline.
5. Enqueues a durable, version-bound embedding job in the inventory transaction and returns the saved resource plus queued processing state.
6. Clients poll the owner-authorized embedding-job route; worker/provider failure does not remove the saved listing and can be retried safely.

Manual listing creation can also persist a JSON object in `details` (maximum 32 KB) and `photo_sharing_preference`. Authenticated listing owners upload up to 12 JPG/PNG/WEBP/MP4/MOV/WEBM files through `POST /api/v1/listings/{listingId}/media`. Images are limited to 10 MB and videos to 100 MB by default. Media bytes live outside the web root in Development and in a private S3 prefix outside Development. Reads stream through the authenticated, match-party-checked API; production configuration must keep the bucket/prefix private.

Migration `20260828000100_AddListingDetailsAndMedia` creates the two additive tables. Migration `20260828000200_AddRequirementMatchingPreferences` adds requirement range/radius/project columns and updates the active requirements view. Both must be applied explicitly in each target database because startup migrations remain disabled by default. Apply and verify `scripts/harden-matching-engine.sql` after the migrations; compiling the API does not install the procedure.

`POST /api/v1/listings/whatsapp-intake` is anonymous for processor/Lambda compatibility and accepts a broker ID. It must be protected by an internal network or API gateway policy before public deployment.

### Requirement

`POST /api/v1/requirements`:

1. Validates fixed/flexible budget semantics, minimum/maximum size, up to five same-city preferred localities, GPS coordinates, radius from 0-100 km, optional project names, and property type.
2. Resolves the broker from the authenticated user.
3. Normalizes `RENTAL` to `RENT` and buy variants to `BUY`.
4. Creates the canonical requirement with optional `budget_min`, `size_max`, `radius_km`, and `preferred_project_names`. Historical callers remain compatible: fixed budget is the default, one legacy locality is accepted, and a missing stored radius matches at 3 km.
5. Enqueues a durable, version-bound embedding job in the same transaction.
6. Returns the saved requirement with queued processing state; owner-authorized polling/retry reports eventual completion or failure.

Manual listing and requirement creation resolve the submitted/geocoded city, locality, latitude, and longitude into the canonical `master` catalogue in the same database transaction as inventory creation. Listings persist the resulting `MasterId`; requirements persist one to five resolved IDs in `PreferredLocalityIds`. Existing catalogue coordinates are retained, and missing catalogue rows are created under a transaction-level advisory lock.

Canonical master rows also persist location provenance: `geocoding_status`, provider/place ID, formatted address, precision, confidence, timestamp, error, and review flag. Coordinates selected in the manual Google Maps flow are stored as `verified`/`user`; server-geocoded imports are stored as `resolved`/`google`. Listings and requirements carry their own resolution status, note, and timestamp. Only `resolved` or `verified` canonical locations may participate in nearby search or automatic matching.

New manual inventory passes through one normalization layer before persistence. Property-type aliases, BHK spacing, furnishing aliases such as `BARE`/`UNFURNISHED`, and facing abbreviations are stored using canonical values. The procedure applies equivalent normalization at read time so historical records do not require an immediate destructive backfill. Listing creation now persists the existing canonical `floor_number`, `road_info`, `price_status`, and `project_name` fields when supplied by the mobile form.

The mobile listing and requirement forms geocode the property/preferred locality text. They must not attach the broker's current GPS position to an inventory record unless that position is explicitly the property/preferred location.

## Embedding pipeline

## Bulk TXT import pipeline

Mobile bulk uploads use the authenticated `POST /api/v1/bulk-imports/uploads` endpoint, including `defaultCity`, upload the returned presigned URL directly to S3, then call `POST /api/v1/bulk-imports/{jobId}/complete`. The UI initializes the fallback from the user's selected city and uses `Indore` when none exists or the field is blank. This fallback is applied only when an extracted record has no explicit city; an explicitly named city always wins. The API records the fallback and original filename on the broker-owned `bulk_import_jobs` row before issuing the URL. `BulkImportJobWorker` passes that original filename to the processor, so `listings.group_name` and `requirements.group_name` retain a human-readable import source rather than the generated storage key. It then resolves locations with Google server-side Geocoding, embeds both targets, and runs matching asynchronously. Job status, fallback city, and counts are available through `GET /api/v1/bulk-imports/{jobId}`; failed jobs can be requeued through `POST /api/v1/bulk-imports/{jobId}/retry`.

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

- `embedding_jobs`, `EmbeddingJobWorker`, `GET /api/v1/embedding-jobs/{jobId}`, and the owner-authorized retry route are wired to manual listing/requirement creates and matching-relevant edits.
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

The database tiers, canonical `UserMatchesService` aggregate buckets, and mobile labels use 80/60 thresholds. The divergent legacy match-details controller was removed during API cleanup.

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
- Insufficient credit sets the request to `credit_required`; it must not create a reveal or partial ledger entries. A later top-up retry remains bound to the original fixed attempt deadline and inventory versions.
- Repeated confirmation taps in one attempt are idempotent and never extend or rewrite the original consent deadline.
- Only a broker who is a party to the match can confirm/reveal.
- Only the receiving broker can accept or reject a pending request.
- Rejection resets confirmations, reveals nothing, and deducts nothing.
- Expiration reveals nothing and deducts nothing.

When background workers are enabled, `ConnectionExpiryWorker` runs the same bounded, idempotent transition used by the protected expiry endpoint. Each batch closes overdue pending/credit-required attempts, clears their current consent proof without deleting the evidence row, resets an otherwise eligible match to Matched, and creates at most one expiry outcome notification. Worker interval and batch size are bounded configuration values.

Retired direct reveal/unlock endpoints are not part of the active HTTP surface. The internal service implementation retains an idempotent completion method used only behind the canonical confirmation flow and tests.

## Wallet accounting

All current wallet mutations use `WalletAccountingService` under row locks. Signup creates the current-period grant exactly once; purchases credit paid tokens; reveal and approved internal adjustments debit free tokens before paid tokens; lazy settlement runs before spending. Monthly settlement expires only the actual unused free balance, preserves paid tokens, and writes a separately keyed grant. The accounting month is the calendar month in Asia/Kolkata, represented by UTC instants (month start is 18:30 UTC on the preceding date). The internal monthly endpoint processes a bounded batch of due wallets linked to active, email- and mobile-verified accounts; it does not create wallets for unverified imported brokers.

## Current API surface

All routes are under `/api/v1` unless stated otherwise.

| Area | Current routes used or supported |
| --- | --- |
| Authentication | `POST /auth/register`, `/auth/login`, email/mobile OTP send and verify, resend, logout |
| Listings | `GET /listings/mine`, `POST /listings`, `POST /listings/{id}/media`, `GET /listings/{id}`, `GET /listings`, `PATCH /listings/{id}`, anonymous `/listings/whatsapp-intake` |
| Requirements | `GET /requirements/mine`, `POST /requirements` |
| Search | `POST /search/properties` |
| Matches | `GET /user-matches`, `GET /user-matches/matches/{id}/details`, authenticated match media, confirm, reject |
| Broker data | broker get/update, wallet, ledger, notifications, notification preferences; legacy broker-match alias remains a migration candidate |
| Wallet/payment | credit packs, canonical Razorpay order/verify/webhook, internal monthly grant/deduct |
| File processor | process, embed, ingest, matches, listing, presigned upload, full pipeline callback |
| Operations | matching run/expiry check, `/hello`, Swagger/OpenAPI |

The mobile Axios interceptor logs out on 401 and does not call refresh. The retired refresh action was removed; persistent refresh sessions require a new end-to-end contract. Access JWTs are reusable until expiry, not single-use tokens.

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

- MSG91 OTP Widget support is disabled by default (`Msg91:WidgetEnabled`). With it enabled, mobile send/resend requires `supportsWidget: true` and returns `success`, legacy `status`, and a `widget` object containing `challengeId`, widget-scoped `tokenAuth`, `widgetId`, expected `identifier` and expiry. It does not generate or send a local OTP. Older apps receive an update-required error; legacy `/auth/verify-otp` is rejected while widget mode is active. Email verification and password login remain unchanged.
- `POST /auth/verify-widget-otp` accepts only a challenge ID and transient MSG91 access token. The backend POSTs to MSG91 `/api/v5/widget/verifyAccessToken` with its private Authkey header, checks HTTP and application success and the exact `91`-prefixed Indian mobile, then checks authenticated JWT issuance/expiry. Proof must be issued after the challenge (whole-second precision), not future-issued or expired. Missing/malformed timestamps fail closed. This JWT contract needs a real-provider smoke test before rollout.
- `widget_otp_challenges` binds a 15-minute challenge to a specific user or staged registration. Atomic consumption and a unique SHA-256 token-hash index prevent challenge/token replay across instances. No raw provider tokens or OTPs are stored. Promotion uses the same email-proof, active-account, broker-identity and wallet transaction as legacy verification. Consumed hashes must be retained; no automatic deletion is implemented. Per-number admission is capped at five challenges per 15 minutes under a PostgreSQL advisory lock; mobile endpoints also have a 10/minute per-remote-IP limiter per process. Provider-side client-token controls and edge/distributed rate limiting remain necessary because clients can call the SDK directly.
- Migration `20260909075218_AddWidgetOtpChallenges` is installed in development but must still be applied and verified independently in every other target before enabling. Backend secrets are `MSG91_AUTH_KEY`, `MSG91_WIDGET_ID`, and `MSG91_WIDGET_TOKEN_AUTH`; the legacy `MSG91_OTP_TEMPLATE_ID` is not used in widget mode. See `scripts/MSG91_WIDGET_SETUP.md` for rollout, secure configuration and live-test requirements.
- Google Maps SDK configuration in the mobile client, the backend Geocoding API key, and the Vertex AI service account are separate credentials with separate restrictions.
- AWS credentials should come from workload roles/OIDC and Secrets Manager. Static AWS keys must not be committed.
- Razorpay order verification and webhook handling must remain idempotent; successful payment credits the canonical wallet and ledger once.
- File-processor and internal matching/credit operations fail closed unless the configured internal service key is present. Network/API-gateway restrictions remain an additional deployment layer.
- Browser CORS is allow-list based, production API documentation defaults off, and configuration validation rejects unsafe required-integration settings.
- `/health/live` reports process liveness; `/health/ready` checks database connectivity plus required schema/procedure compatibility with bounded timeouts.

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
