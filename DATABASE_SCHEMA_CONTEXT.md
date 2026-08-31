# PropSeekr canonical database design

Last audited against the old `postgres` database, the `propseekr_v2` database,
the EF model, migrations, and deployed routines on 2026-08-30.

This file is the schema-level companion to `APPLICATION_CONTEXT.md`. Do not put
credentials, connection strings, customer text, or tokens here.

## Design authority

The current application uses physical `listings` and `requirements` tables.
The old database's `listings`/`requirements` views over `listings_table` and
`requirements_table` are not the v2 design and must not be recreated in v2.

The canonical graph is:

```text
Users -> brokers
brokers -> listings -> listing_details + listing_media + listing_sizes
brokers -> requirements
listings + requirements -> matches
matches -> match_connection_requests + match_confirmations + reveals
brokers -> credit_wallets + credit_transactions + notifications
master -> listings.master_id
master <- requirements.preferred_locality_ids[]
```

`preferred_locality_ids` is an integer array, so PostgreSQL cannot enforce an
element-level foreign key. Every API/processor writer must resolve IDs through
`master`, and schema audits must check every array element for orphans.
`embedding_jobs` is intentionally polymorphic (`listing` or `requirement`) and
therefore also requires application/audit validation rather than a normal FK.

## Canonical entities

- Identity: `Users`, `brokers`, OTP/email OTP records.
- Inventory: `listings`, `requirements`, `master`, `listing_sizes`,
  `listing_details`, `listing_media`.
- Matching and consent: `matches`, `match_connection_requests`,
  `match_confirmations`, `reveals`, numeric broker `notifications`.
- Tokens and payments: `credit_wallets`, `credit_transactions`, `credit_packs`,
  `PaymentTransactions`; `payments` is a compatibility payment surface.
- Background work: `embedding_jobs`, `bulk_import_jobs`, `processed_files`.
- Secondary workflow: `listing_requirements`, `notification_preferences`,
  `deals`, `visits`, `disputes`.

The archive migration `20260831180804_RetireLegacyCompatibilityTables` removed
empty compatibility tables `PropertyRequests`, `UnlockedProperties`, GUID
`Notifications`, `payments`, `match_statuses`, `deals`, `visits`, and
`disputes`. Lowercase old `users`,
`converted_text`, and `payment_orders` exist only in the old database and have
no current API code references, so they must not be copied into v2 merely to
make schemas textually identical.

Old `search_vector` columns/triggers and the old `fn_get_*matches` overloads are
also not canonical v2 dependencies. Current nearby search uses structured
filters and coordinates. The internal file-processor matches compatibility
endpoint queries canonical tables directly.

## Required integrity and indexes

Migration `20260830142328_ReconcileCanonicalDatabaseDesign` is the v2
reconciliation migration. It:

- adds missing FKs for listing locality, listing details/media, bulk-import
  ownership, connection-request parties, and linked notifications;
- restores uniqueness for identity/KYC keys, payment idempotency keys,
  listing-requirement pairs, one preference per broker, one deal per match, and
  one active connection request per match;
- restores OTP, inventory, matching, notification, ledger, expiry, GIN array,
  locality trigram, and canonical search indexes;
- protects role, coordinate, radius, match score/cross-broker, wallet, size,
  media, and details-JSON invariants;
- normalizes broker defaults shared by EF and raw ingestion;
- merges exact case/whitespace duplicate master localities only after repointing
  every listing and requirement reference.

The matching procedure is deployed separately from EF migrations:

```text
scripts/matching-engine-schema.sql
scripts/harden-matching-engine.sql
```

After deployment, verify that the installed two-argument
`sp_run_matching_engine(integer, integer)` contains the progressed-match
preservation predicate, same-broker exclusion, city/locality gates, fixed-budget
ceiling, 35-point floor, per-requirement cap, and embedding-model guard.

## Audit findings that require data remediation

The 2026-08-30 v2 audit found no orphan canonical FKs, invalid match ownership,
same-broker matches, negative wallets, duplicate match pairs, or invalid current
radius/score/media values.

Historical WhatsApp imports still contain:

- 2,302 active listings without `master_id` or a stored city;
- 522 active requirements without preferred locality IDs or a stored city;

The active 2026-08-30 import subsequently completed and all 3,753 listings and
1,061 requirements now have embeddings. Keep the audit-script embedding checks:
future failed or manually copied inventory can reintroduce this gap.

Eight normalized locality groups currently contain 13 redundant rows beyond
their keeper records (21 rows total). The reconciliation migration repoints
listing and ordered requirement-array references before removing those 13
duplicates and enforcing normalized city/area uniqueness.

Rows without a real locality cannot participate in nearby search or the strict
matching procedure. Bulk import may use its explicit upload fallback city; when
the caller provides none, that fallback is Indore. The fallback supplies city
context only—it must not fabricate a locality or attach the uploader's GPS.
Reprocess these rows from their source text with confidence-gated Google
geocoding, then embed them. Missing vectors reduce semantic score but do not
override hard location, transaction, property, configuration, or budget rules.

Migration `20260830153252_AddTrustedLocationResolution` adds provider/confidence/
review metadata to `master`, per-record location resolution state to listings and
requirements, `default_city` to durable bulk imports, and the resumable
`location_remediation_jobs` table. The matching SQL and nearby search accept only
canonical master rows whose geocoding status is `resolved` or `verified`.

## Verification checklist

For every target database:

1. Confirm the configured database name before any write.
2. Compare physical tables/columns with the EF model; do not trust
   `__EFMigrationsHistory` alone.
3. Check PK/UQ/FK/check constraints, index definitions, identity sequences,
   extensions, routines, and triggers.
4. Check all FK and array/polymorphic references for orphans before adding a
   relationship.
5. Apply the reconciliation migration explicitly.
6. Apply and verify the current matching SQL explicitly.
7. Run focused API tests plus a known listing/requirement false-positive and
   true-positive matching smoke test.
8. Record unresolved data-remediation counts; never silently invent location,
   price, ownership, availability, or contact data.
