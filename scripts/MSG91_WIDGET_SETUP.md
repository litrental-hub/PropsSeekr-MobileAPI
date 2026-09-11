# MSG91 mobile OTP rollout

This integration replaces the **mobile** OTP step for login and registration. Registration still requires the existing email OTP first; password login and email delivery are not migrated. Source changes do not enable the provider, send messages, apply migrations or deploy either app.

## Configuration

In MSG91, enable **Mobile Integration** on the OTP widget. Keep the India-only restriction. Use a widget-scoped token, not the account Authkey, for client integration. Validate that configured SMS and retry channels work with the account's actual trial balance and templates; dashboard selection does not guarantee delivery or free usage. Do not add demo bypass credentials for production.

Add these flat string keys to the API's configured AWS Secrets Manager secret using your secure console/CLI session:

| Secret key | Value source | Exposure |
| --- | --- | --- |
| `MSG91_AUTH_KEY` | MSG91 Server Side Integration account Authkey | Backend only |
| `MSG91_WIDGET_ID` | Widget ID from Client Side Integration | Client configuration |
| `MSG91_WIDGET_TOKEN_AUTH` | Widget-scoped client token | Intentionally sent to mobile SDK |

The widget token provided in chat is **not** the server Authkey. Do not paste the server Authkey into chat, source, appsettings, logs, or mobile code. Prefer a fresh restricted client token before production and keep provider throttles enabled. `MSG91_OTP_TEMPLATE_ID` is only for the old SendOTP adapter, not this widget.

Enable using the non-secret server setting `Msg91__WidgetEnabled=true` only after the migration, secrets and compatible mobile release are ready. It defaults to false. If enabled without required credentials, requests fail closed; they never silently fall back to legacy SMS.

## Deployment order

1. Review and explicitly apply additive migration `20260909075218_AddWidgetOtpChallenges` to the intended database. Do not blindly apply unrelated pending destructive migrations. Check migration history first; startup migration remains disabled.
2. Deploy the API with widget mode **off**, then install/release the compatible mobile app. `npm ci` must run the committed `patch-package` postinstall. Run `bundle exec pod install` inside `ios`; rebuild both native applications.
3. Add all three configuration values securely. Enable Mobile Integration in MSG91 and test on a staging deployment with widget mode on before production rollout.
4. Enable widget mode in production only when older mobile versions have an upgrade path. Old clients missing `supportsWidget: true` receive an update-required error; local-code verification is disabled in widget mode. Password login remains available.
5. Configure edge/distributed rate limits and MSG91 client-token usage limits. The app's IP limiter is per API process; verify proxy/IP forwarding configuration. Direct SDK sends do not pass through PropSeekr's admission control.

## Verification contract and security

Send/resend returns a challenge, not confirmation that an SMS was sent. The DefaultWidget reads the OTP length from the provider configuration (currently six digits), and owns OTP entry, retry channels and its provider timers. PropSeekr's challenge expires after 15 minutes independently of provider expiry. SDK 3.0.0 has no identifier prefill or lock; users must enter the same number shown in PropSeekr. Email or another mobile cannot complete the mobile step.

The backend calls `POST https://api.msg91.com/api/v5/widget/verifyAccessToken`, using an `authkey` header and JSON `{ "access-token": "<transient-provider-token>" }`. The documented success response is `{ "type": "success", "message": "91<10-digit-mobile>" }`. Only this authenticated response establishes identity. The backend then requires numeric JWT `iat` and `exp`, issuance at/after the challenge (Unix-second precision), no more than 30 seconds future clock skew, and unexpired proof. Verify these claims using real SDK OTP and invisible-verification results before enabling; unsupported/malformed claims are rejected, never trusted from a client callback.

The server binds proof to the original user or pending registration, atomically consumes the challenge and stores only a unique SHA-256 token hash. It then rechecks email proof and active-account rules and creates/claims the broker and ten-credit wallet in the same transaction. Keep consumed hashes; no retention cleanup job is included. Never log provider tokens, credentials, raw SDK results, or Axios request/error objects. A committed SDK patch also removes native URL and error-object logging and fixes React Native 0.85 style/type compatibility.

## Acceptance checklist

- Existing-user SMS login, complete email-then-mobile registration, and correct broker/wallet creation.
- Inactive user, email-unverified regular user, expired registration, wrong number, non-India number and email result rejected.
- Incorrect/expired OTP, close/cancel/reopen, provider outage, backend failure and app background/resume recover without premature login.
- Duplicate callbacks, reused token on the same/different challenge, concurrent completion and expired challenge rejected without extra users/wallets.
- Real Android and iOS SMS/resend plus every enabled retry channel. Test invisible verification on an actual SIM/mobile network as well as Wi-Fi fallback.
- Test dashboard captcha behavior with this mobile SDK; do not disable anti-abuse controls silently to make a test pass. Confirm provider-supported settings if it fails.
- Provider-signed JWT `iat`/`exp` contract and server clock synchronization checked. No live provider checks have run without the backend Authkey.

Official references: [React Native SDK](https://github.com/Walkover-Web-Solution/OTPSDK_ReactNative), [widget setup](https://msg91.com/help/sendotp/how-to-integrate-the-new-login-with-otp-widget), [server token verification](https://docs.msg91.com/otp-widget/verify-access-token).

## Local verification (2026-09-09)

Mobile: 87 tests pass, TypeScript and ESLint pass, Android unsigned release and iOS unsigned simulator release builds pass. API: build passes with three pre-existing warnings; 93 tests pass in the default run (25 PostgreSQL-dependent tests skipped). All 13 registration/OTP transaction tests, including the widget concurrency/replay checks, also pass against a separate temporary localhost PostgreSQL cluster; that test server was then stopped. The remaining 12 database-dependent tests were not run here. No production database, credentials, provider configuration, live messages or deployed application was changed.
