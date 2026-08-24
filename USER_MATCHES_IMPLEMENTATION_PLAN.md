# User Matches implementation and end-to-end test plan

## Scope and invariants

- The authenticated user's broker identity is resolved on the server. A client-supplied broker ID is never trusted.
- A match can be confirmed only by its listing or requirement broker and only after all checklist items are accepted.
- Contact details remain masked until both parties confirm inside the active window and one atomic reveal succeeds.
- A successful reveal creates exactly one reveal record, exactly two wallet ledger records, and deducts each party exactly once. Free credits are consumed before paid credits.
- Retries and concurrent reveal requests are idempotent. A failed reveal, including insufficient credits, leaves balances and reveal records unchanged.
- `matches.state` owns the confirmation/reveal lifecycle; the independent matching `status` field is not overwritten by this flow.

## Delivery plan

1. Stabilize the existing solution and document the current API/UI contract.
2. Implement the broker-scoped matches query, filtering, pagination, confirmation state, countdown data, and revealed-contact projection.
3. Consolidate confirmation and reveal behind one transactional service while keeping authenticated compatibility wrappers for old routes.
4. Update login identity responses and the React Native API client so all match actions use the canonical authenticated API.
5. Update the Matches screen for pending, confirmed, expired, and revealed states; add refresh polling, app-resume refresh, countdowns, and insufficient-credit handling.
6. Add regression coverage at service, API-contract, frontend-state, native-build, and mobile-journey levels.
7. Deploy schema-compatible code to a non-production environment, run the seeded two-broker journey, monitor errors and wallet/reveal invariants, then promote with rollback available.

## End-to-end test matrix

| Layer | Scenario | Expected result |
| --- | --- | --- |
| Backend integration | First broker confirms | Match is pending; no reveal or deductions |
| Backend integration | Second broker confirms | One reveal, two ledger rows, both balances deducted once |
| Backend integration | Reveal is retried/concurrent | Existing reveal is returned; no duplicate charge |
| Backend integration | Either wallet lacks credits | No reveal and no wallet/ledger changes |
| Backend integration | Non-party confirms/reveals | HTTP/service authorization failure |
| API smoke | Unauthenticated list | 401 |
| API smoke | Authenticated two-user flow | Correct masked-to-revealed state and counterparty contact |
| Frontend unit | State/action/countdown mapping | Stable CTA and expiry behavior |
| Android build | Debug APK | Compiles with the real native dependency graph |
| Mobile UI | Broker A confirms, Broker B accepts, Broker A refreshes | Both users see only the other broker's revealed contact |

## Environment inputs for staging

Local integration tests use a disposable PostgreSQL database and synthetic users, so no shared credentials are required. Before running the same suite against staging, provide:

- a staging API base URL;
- a dedicated, resettable PostgreSQL test database connection string (not production);
- two staging broker test accounts attached to opposite sides of a known match;
- permission to reset only those test wallets, confirmations, ledgers, and reveal rows.

Secrets must be supplied through environment variables or the deployment secret store, never committed to source control.
