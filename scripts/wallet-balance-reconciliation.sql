-- Read-only wallet reconciliation report.
-- credit_wallets is authoritative. Users.Credits and brokers.credit_balance are
-- compatibility mirrors only; this script intentionally performs no updates.

WITH wallet_totals AS (
    SELECT
        w.broker_id,
        w.free_credits_balance,
        w.paid_credits_balance,
        w.free_credits_balance + w.paid_credits_balance AS wallet_total,
        w.updated_at
    FROM credit_wallets w
), linked_accounts AS (
    SELECT
        u."Id" AS user_id,
        u."BrokerId" AS broker_id,
        u."Credits" AS user_legacy_total,
        b.credit_balance AS broker_legacy_total
    FROM "Users" u
    LEFT JOIN brokers b ON b.brokerid = u."BrokerId"
    WHERE u."BrokerId" IS NOT NULL
)
SELECT
    a.broker_id,
    w.free_credits_balance,
    w.paid_credits_balance,
    w.wallet_total,
    a.user_legacy_total,
    a.broker_legacy_total,
    CASE
        WHEN w.broker_id IS NULL THEN 'MISSING_WALLET'
        WHEN a.user_legacy_total IS DISTINCT FROM w.wallet_total
          OR a.broker_legacy_total IS DISTINCT FROM w.wallet_total THEN 'LEGACY_MIRROR_MISMATCH'
        ELSE 'OK'
    END AS reconciliation_status,
    w.updated_at AS wallet_updated_at
FROM linked_accounts a
LEFT JOIN wallet_totals w ON w.broker_id = a.broker_id
ORDER BY reconciliation_status DESC, a.broker_id;

-- Successful transactions created by the legacy singular payment flow may not
-- have a wallet-ledger row. Do not replay them automatically: some may already
-- have been reconciled manually. Review each candidate against Razorpay and the
-- wallet balance before applying any compensating transaction.
SELECT
    p."Id" AS payment_transaction_id,
    p."RazorpayOrderId" AS razorpay_order_id,
    p."CreditsAwarded" AS credits_awarded,
    p."ModifiedDate" AS payment_completed_at,
    u."BrokerId" AS broker_id,
    CASE WHEN ct."Id" IS NULL THEN 'REVIEW_REQUIRED' ELSE 'LEDGER_PRESENT' END AS ledger_status
FROM "PaymentTransactions" p
JOIN "Users" u ON u."Id" = p."UserId"
LEFT JOIN credit_transactions ct
    ON ct.broker_id = u."BrokerId"
   AND ct.reference_type = 'payment'
   AND ct.reference_key = replace(p."Id"::text, '-', '')
WHERE lower(p."Status") = 'success'
ORDER BY p."ModifiedDate" DESC;
