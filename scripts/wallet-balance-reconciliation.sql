-- Read-only wallet coverage report. credit_wallets is the sole authoritative
-- balance store; this script intentionally performs no updates.

WITH wallet_totals AS (
    SELECT
        w.broker_id,
        w.free_credits_balance,
        w.paid_credits_balance,
        w.free_credits_balance + w.paid_credits_balance AS wallet_total,
        w.updated_at
    FROM credit_wallets w
)
SELECT
    b.brokerid AS broker_id,
    w.free_credits_balance,
    w.paid_credits_balance,
    w.wallet_total,
    CASE
        WHEN w.broker_id IS NULL THEN 'MISSING_WALLET'
        ELSE 'OK'
    END AS reconciliation_status,
    w.updated_at AS wallet_updated_at
FROM "Users" u
JOIN brokers b ON b.brokerid = u."BrokerId"
LEFT JOIN wallet_totals w ON w.broker_id = b.brokerid
WHERE u."BrokerId" IS NOT NULL
ORDER BY reconciliation_status DESC, b.brokerid;

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
