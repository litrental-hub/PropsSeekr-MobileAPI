# PropSeekr API working rules

Read `APPLICATION_CONTEXT.md` before changing this repository. If a change alters a business rule, API contract, database source, state transition, integration, or operational requirement, update that document in the same change.

Core invariants:

- Authenticated broker operations derive `BrokerId` from the JWT user; never trust a client-supplied broker ID.
- Admin inventory and match reads are intentionally unscoped but remain paginated and filtered.
- Canonical matching uses listings, requirements, matches, broker wallets/ledger, connection requests, confirmations, and reveals. Do not extend legacy `PropertyRequests`, `User.Credits`, `UnlockedProperty`, or GUID notification flows for new match features.
- Listing/requirement creation must report whether Gemini embedding and targeted stored-procedure matching completed.
- The matching procedure must reject self, transaction, property, city, locality, configuration, and fixed-budget incompatibilities before scoring. Preserve progressed matches when rebuilding automatic matches.
- A regular contact unlock always requires both brokers to confirm. Reveal and one-token deduction from each wallet are atomic and idempotent. Never expose contact data based on state alone; require a reveal record.
- WhatsApp delivery is planned, not active. Do not claim a message was sent.
- Never commit credentials, private keys, connection strings, API keys, access tokens, or production data.
- Database procedure deployment is explicit. Verify the procedure installed in the target database instead of assuming a code build or EF migration changed it.

Run the smallest relevant tests plus a build for every change. Matching, wallet, identity, and authorization changes require focused regression tests.
