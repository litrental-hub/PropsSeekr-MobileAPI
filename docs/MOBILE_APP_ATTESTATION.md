# Mobile app attestation integration

Attestation is required for `POST /api/v1/payment/order`, `POST /api/v1/payment/verify`, and `POST /api/v1/user-matches/unlock` when `AppAttestation:EnforcementMode` is `Enforce`. Send a valid Cognito **access token** for every attestation route and sensitive action.

## Canonical request binding

For a sensitive request, calculate `bodyHash = SHA-256(UTF-8 request body bytes)`. Construct this UTF-8 string with LF (`\n`) separators and no trailing LF:

`METHOD\nnormalized-lowercase-route\nlocal-user-guid\nchallenge-guid\nlowercase-hex-bodyHash`

The `X-App-Request-Hash` value is base64url without padding of `SHA-256(canonical request UTF-8 bytes)`. The backend independently calculates it from the received body, route, authenticated local user GUID, and challenge ID.

## Shared challenge flow

Call `POST /api/v1/attestation/challenge`:

```json
{ "platform": "android", "purpose": "PaymentOrder" }
```

Allowed purposes: `PaymentOrder`, `PaymentVerify`, `PropertyUnlock`, and `AppAttestEnroll` (iOS key enrollment only). The response contains `challengeId`, a random `nonce`, and `expiresAt`. A challenge expires in five minutes, belongs to the authenticated user/platform/purpose, and is single-use.

## Android — Google Play Integrity

Immediately before the sensitive request, request a standard Play Integrity token using the canonical request hash as the Play Integrity `requestHash`. Send:

```
X-App-Attestation-Platform: android
X-App-Attestation-Challenge-Id: <challengeId>
X-App-Request-Hash: <base64url canonical hash>
X-App-Attestation-Token: <Play Integrity token>
```

Call `POST /api/v1/attestation/android/verify` with `challengeId`, `integrityToken`, and the request hash. The backend sends the token to Google's `decodeIntegrityToken` API using Application Default Credentials, then validates hash, freshness, package name, `PLAY_RECOGNIZED`, device integrity, and licensing verdict. It returns `ALLOW` only after verified request-binding state is stored. Then send the sensitive request with `X-App-Attestation-Challenge-Id`; the API atomically consumes the matching verified challenge after independently hashing its received body.

## iOS — Apple App Attest

### Key enrollment

Request an `ios` / `AppAttestEnroll` challenge. Generate an App Attest key, calculate `clientDataHash = SHA-256(UTF-8 nonce)`, create the Apple attestation object, then call `POST /api/v1/attestation/ios/enroll`:

```json
{ "challengeId": "<guid>", "keyId": "<Apple key id>", "attestationObject": "<base64url CBOR>", "appVersion": "<optional>" }
```

The backend validates Apple's certificate chain, application identifier, attestation nonce, and credential public key before storing the public key. Device private keys never leave iOS.

### Sensitive assertion

Request an `ios` challenge for the exact purpose. Compute the canonical request hash above; its decoded 32-byte value is passed to `generateAssertion(keyId, clientDataHash)`. Send the normal sensitive request with:

```
X-App-Attestation-Platform: ios
X-App-Attestation-Challenge-Id: <challengeId>
X-App-Request-Hash: <base64url canonical hash>
X-App-Attestation-Key-Id: <Apple key id>
X-App-Attestation-Assertion: <base64url CBOR assertion>
```

Call `POST /api/v1/attestation/ios/assert` with `challengeId`, `keyId`, `assertion`, and `requestHash`. It returns `ALLOW` only after the assertion is verified. Then send the sensitive request with `X-App-Attestation-Challenge-Id`; the API atomically consumes the matching verified challenge. The assertion is rejected if its signature, app ID hash, key/user binding, counter, challenge, route/body hash, or revocation state is invalid.
