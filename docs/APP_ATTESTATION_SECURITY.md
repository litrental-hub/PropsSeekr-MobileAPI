# App attestation threat model

Cognito access tokens identify an authenticated customer; they do not prove that the request came from an unmodified PropSeekr mobile application. App attestation adds provider-verified evidence for selected high-risk operations.

It helps reduce modified APK/IPA use, unofficial builds, automated non-genuine clients, request replay, request tampering, and use of a stolen customer token from an unregistered app instance. It does not replace Cognito, payment ownership checks, Razorpay HMAC validation, or server-side authorization.

The API uses server-generated one-time challenges tied to user, platform, purpose, and five-minute expiry. Attestation is bound to a hash over method, normalized route, authenticated local user ID, challenge ID, and received request body. Reusing a challenge, changing a body/route, using another user/key, or replaying an iOS assertion counter fails verification.

Android tokens are verified by Google server-side using application default credentials; the API does not trust decoded client data. Apple App Attest validates Apple's certificate chain and device assertion signature against the enrolled public key. Private device keys and provider service-account credentials are never stored in this repository.

Limitations remain: rooted/jailbroken or compromised authenticated devices can receive different provider verdicts depending on platform policy; no attestation system makes a device perfectly trustworthy. Attestation is intentionally limited to payment creation, payment verification, and property unlock, not login, OTP, browsing, webhooks, or admin APIs.
