using System.Formats.Asn1;
using System.Formats.Cbor;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Google.Apis.Auth.OAuth2;
using Microsoft.EntityFrameworkCore;
using PropSeekr.Data;
using PropSeekr.DTOs.Attestation;
using PropSeekr.Models;
using PropSeekr.Services.Interfaces;

namespace PropSeekr.Services;

public sealed class AppAttestationService : IAppAttestationService
{
    private const string Android = "android";
    private const string Ios = "ios";
    private readonly AppDbContext _db;
    private readonly IConfiguration _configuration;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<AppAttestationService> _logger;

    public AppAttestationService(AppDbContext db, IConfiguration configuration, IHttpClientFactory httpClientFactory, ILogger<AppAttestationService> logger)
    {
        _db = db;
        _configuration = configuration;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<CreateAttestationChallengeResponseDto> CreateChallengeAsync(Guid userId, CreateAttestationChallengeRequestDto request, CancellationToken cancellationToken)
    {
        var platform = request.Platform.Trim().ToLowerInvariant();
        var nonce = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = new AppAttestationChallenge
        {
            Id = Guid.NewGuid(), UserId = userId, Platform = platform, Purpose = request.Purpose,
            Nonce = nonce, ExpiresAt = DateTime.UtcNow.AddMinutes(5)
        };
        _db.AppAttestationChallenges.Add(challenge);
        await _db.SaveChangesAsync(cancellationToken);
        return new CreateAttestationChallengeResponseDto { ChallengeId = challenge.Id, Nonce = nonce, ExpiresAt = challenge.ExpiresAt };
    }

    public async Task EnrollAppleAppAttestAsync(Guid userId, EnrollAppAttestRequestDto request, CancellationToken cancellationToken)
    {
        var challenge = await GetChallengeAsync(request.ChallengeId, userId, Ios, "AppAttestEnroll", cancellationToken);
        var attestation = DecodeBase64Url(request.AttestationObject);
        var (authData, leafCertificate) = ParseAndValidateAppleAttestation(attestation, challenge.Nonce);
        using var publicKey = ExtractAppleCredentialPublicKey(authData, request.KeyId);
        ValidateAppleAuthenticatorData(authData, isAssertion: false, out _);

        if (await _db.TrustedAppInstances.AnyAsync(x => x.Platform == Ios && x.KeyId == request.KeyId && x.UserId != userId, cancellationToken))
            throw new InvalidOperationException("This App Attest key is already associated with another account.");

        var instance = await _db.TrustedAppInstances.FirstOrDefaultAsync(x => x.Platform == Ios && x.KeyId == request.KeyId, cancellationToken);
        if (instance is null)
        {
            instance = new TrustedAppInstance { Id = Guid.NewGuid(), UserId = userId, Platform = Ios, KeyId = request.KeyId };
            _db.TrustedAppInstances.Add(instance);
        }

        instance.PublicKeySpkiBase64 = Convert.ToBase64String(publicKey.ExportSubjectPublicKeyInfo());
        instance.Status = "Trusted"; instance.IsRevoked = false; instance.RevokedAt = null;
        instance.AppVersion = request.AppVersion; instance.Environment = GetRequired("AppAttestation:iOS:Environment");
        var now = DateTime.UtcNow;
        instance.UpdatedAt = now;
        instance.LastSeenAt = now;
        challenge.UsedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
        leafCertificate.Dispose();
    }

    public async Task<AppAttestationVerificationResult> VerifySensitiveRequestAsync(HttpContext context, Guid userId, string purpose, CancellationToken cancellationToken)
    {
        var platform = context.Request.Headers["X-App-Attestation-Platform"].ToString().Trim().ToLowerInvariant();
        if (platform is not (Android or Ios)) return new(false, platform, "Missing or invalid platform.");
        if (!Guid.TryParse(context.Request.Headers["X-App-Attestation-Challenge-Id"], out var challengeId)) return new(false, platform, "Missing challenge.");
        var challenge = await GetChallengeAsync(challengeId, userId, platform, purpose, cancellationToken);
        var expectedHash = await CalculateRequestHashAsync(context, userId, challenge.Id, cancellationToken);
        var suppliedHash = context.Request.Headers["X-App-Request-Hash"].ToString();
        if (!FixedTimeEquals(expectedHash, suppliedHash)) return new(false, platform, "Request binding failed.");

        if (platform == Android)
            await VerifyGooglePlayIntegrityAsync(context.Request.Headers["X-App-Attestation-Token"].ToString(), expectedHash, challenge, cancellationToken);
        else
            await VerifyAppleAssertionAsync(userId,
                context.Request.Headers["X-App-Attestation-Key-Id"].ToString(),
                context.Request.Headers["X-App-Attestation-Assertion"].ToString(),
                expectedHash, challenge, cancellationToken);

        challenge.UsedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);
        return new(true, platform, "Verified");
    }

    public async Task<AppAttestationDecisionDto> VerifyPlayIntegrityAsync(Guid userId, VerifyPlayIntegrityRequestDto request, CancellationToken cancellationToken)
    {
        var challenge = await GetChallengeAsync(request.ChallengeId, userId, Android, GetPurposeForChallenge(request.ChallengeId), cancellationToken);
        await VerifyGooglePlayIntegrityAsync(request.IntegrityToken, request.RequestHash, challenge, cancellationToken);
        await MarkVerifiedAsync(challenge, Android, request.RequestHash, cancellationToken);
        return new AppAttestationDecisionDto { Decision = "ALLOW", ChallengeId = challenge.Id };
    }

    public async Task<AppAttestationDecisionDto> VerifyAppleAssertionAsync(Guid userId, VerifyAppAttestAssertionRequestDto request, CancellationToken cancellationToken)
    {
        var challenge = await GetChallengeAsync(request.ChallengeId, userId, Ios, GetPurposeForChallenge(request.ChallengeId), cancellationToken);
        await VerifyAppleAssertionAsync(userId, request.KeyId, request.Assertion, request.RequestHash, challenge, cancellationToken);
        await MarkVerifiedAsync(challenge, Ios, request.RequestHash, cancellationToken);
        return new AppAttestationDecisionDto { Decision = "ALLOW", ChallengeId = challenge.Id };
    }

    public async Task<bool> ConsumeVerifiedRequestAsync(HttpContext context, Guid userId, string purpose, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(context.Request.Headers["X-App-Attestation-Challenge-Id"], out var challengeId)) return false;
        var requestHash = await CalculateRequestHashAsync(context, userId, challengeId, cancellationToken);
        var now = DateTime.UtcNow;
        return await _db.AppAttestationChallenges
            .Where(x => x.Id == challengeId && x.UserId == userId && x.Purpose == purpose && x.UsedAt == null && x.ExpiresAt >= now && x.VerifiedAt != null && x.VerifiedRequestHash == requestHash)
            .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.UsedAt, now), cancellationToken) == 1;
    }

    private async Task VerifyGooglePlayIntegrityAsync(string token, string expectedHash, AppAttestationChallenge challenge, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(token)) throw new InvalidOperationException("Missing Play Integrity token.");
        var packageName = GetRequired("AppAttestation:Android:PackageName");
        _ = GetRequired("AppAttestation:Android:GoogleCloudProjectNumber");
        var credential = await GoogleCredential.GetApplicationDefaultAsync(cancellationToken);
        credential = credential.CreateScoped("https://www.googleapis.com/auth/playintegrity");
        var accessToken = await credential.UnderlyingCredential.GetAccessTokenForRequestAsync(cancellationToken: cancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"https://playintegrity.googleapis.com/v1/{Uri.EscapeDataString(packageName)}:decodeIntegrityToken")
        {
            Content = JsonContent.Create(new { integrity_token = token })
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await _httpClientFactory.CreateClient("PlayIntegrity").SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException("Play Integrity verification was rejected by Google.");
        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
        var payload = document.RootElement.GetProperty("tokenPayloadExternal");
        var requestDetails = payload.GetProperty("requestDetails");
        var receivedHash = requestDetails.TryGetProperty("requestHash", out var hash) ? hash.GetString() : null;
        if (!FixedTimeEquals(expectedHash, receivedHash)) throw new InvalidOperationException("Play Integrity request hash mismatch.");
        if (requestDetails.GetProperty("timestampMillis").GetInt64() < DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeMilliseconds()) throw new InvalidOperationException("Stale Play Integrity token.");
        var appIntegrity = payload.GetProperty("appIntegrity");
        if (!string.Equals(appIntegrity.GetProperty("packageName").GetString(), packageName, StringComparison.Ordinal) ||
            !string.Equals(appIntegrity.GetProperty("appRecognitionVerdict").GetString(), "PLAY_RECOGNIZED", StringComparison.Ordinal))
            throw new InvalidOperationException("Unrecognized Android application.");
        var deviceVerdicts = payload.GetProperty("deviceIntegrity").GetProperty("deviceRecognitionVerdict").EnumerateArray().Select(x => x.GetString());
        if (!deviceVerdicts.Any(x => x is "MEETS_DEVICE_INTEGRITY" or "MEETS_STRONG_INTEGRITY")) throw new InvalidOperationException("Device integrity verdict is insufficient.");
        if (payload.TryGetProperty("accountDetails", out var account) && account.TryGetProperty("appLicensingVerdict", out var licensing) &&
            !string.Equals(licensing.GetString(), "LICENSED", StringComparison.Ordinal)) throw new InvalidOperationException("Application is not licensed.");
    }

    private async Task VerifyAppleAssertionAsync(Guid userId, string keyId, string assertion, string expectedHash, AppAttestationChallenge challenge, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(keyId) || string.IsNullOrWhiteSpace(assertion)) throw new InvalidOperationException("Missing App Attest assertion.");
        var instance = await _db.TrustedAppInstances.SingleOrDefaultAsync(x => x.UserId == userId && x.Platform == Ios && x.KeyId == keyId, cancellationToken);
        if (instance is null || instance.IsRevoked || string.IsNullOrWhiteSpace(instance.PublicKeySpkiBase64)) throw new InvalidOperationException("App Attest key is not trusted.");
        var reader = new CborReader(DecodeBase64Url(assertion), CborConformanceMode.Ctap2Canonical);
        var count = reader.ReadStartMap(); byte[]? authData = null; byte[]? signature = null;
        for (var i = 0; count is null || i < count; i++) { var fieldName = reader.ReadTextString(); if (fieldName == "authenticatorData") authData = reader.ReadByteString(); else if (fieldName == "signature") signature = reader.ReadByteString(); else reader.SkipValue(); }
        reader.ReadEndMap();
        if (authData is null || signature is null) throw new InvalidOperationException("Malformed App Attest assertion.");
        using var key = ECDsa.Create(); key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(instance.PublicKeySpkiBase64), out _);
        ValidateAppleAuthenticatorData(authData, isAssertion: true, out var counter);
        if (counter <= instance.AssertionCounter) throw new InvalidOperationException("Replayed App Attest assertion.");
        var signedData = authData.Concat(DecodeBase64Url(expectedHash)).ToArray();
        if (!key.VerifyData(signedData, signature, HashAlgorithmName.SHA256)) throw new InvalidOperationException("Invalid App Attest assertion signature.");
        instance.AssertionCounter = counter; instance.LastSeenAt = instance.UpdatedAt = DateTime.UtcNow;
    }

    private async Task MarkVerifiedAsync(AppAttestationChallenge challenge, string platform, string requestHash, CancellationToken cancellationToken)
    {
        challenge.VerifiedAt = DateTime.UtcNow;
        challenge.VerifiedPlatform = platform;
        challenge.VerifiedRequestHash = requestHash;
        await _db.SaveChangesAsync(cancellationToken);
    }

    private string GetPurposeForChallenge(Guid challengeId)
    {
        var purpose = _db.AppAttestationChallenges.Where(x => x.Id == challengeId).Select(x => x.Purpose).SingleOrDefault();
        return purpose ?? throw new InvalidOperationException("App attestation challenge was not found.");
    }

    private async Task<AppAttestationChallenge> GetChallengeAsync(Guid id, Guid userId, string platform, string purpose, CancellationToken cancellationToken)
    {
        var challenge = await _db.AppAttestationChallenges.SingleOrDefaultAsync(x => x.Id == id && x.UserId == userId, cancellationToken)
            ?? throw new InvalidOperationException("App attestation challenge was not found.");
        if (challenge.UsedAt is not null || challenge.ExpiresAt < DateTime.UtcNow || challenge.Platform != platform || challenge.Purpose != purpose)
            throw new InvalidOperationException("App attestation challenge is invalid or expired.");
        return challenge;
    }

    private async Task<string> CalculateRequestHashAsync(HttpContext context, Guid userId, Guid challengeId, CancellationToken cancellationToken)
    {
        context.Request.EnableBuffering();
        using var memory = new MemoryStream(); await context.Request.Body.CopyToAsync(memory, cancellationToken); context.Request.Body.Position = 0;
        var bodyHash = SHA256.HashData(memory.ToArray());
        var canonical = $"{context.Request.Method.ToUpperInvariant()}\n{context.Request.Path.Value!.ToLowerInvariant()}\n{userId:D}\n{challengeId:D}\n{Convert.ToHexString(bodyHash).ToLowerInvariant()}";
        return Base64Url(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private (byte[] AuthData, X509Certificate2 LeafCertificate) ParseAndValidateAppleAttestation(byte[] encoded, string nonce)
    {
        var reader = new CborReader(encoded, CborConformanceMode.Ctap2Canonical); var count = reader.ReadStartMap(); byte[]? authData = null; List<byte[]>? certificates = null;
        for (var i = 0; count is null || i < count; i++) { var key = reader.ReadTextString(); if (key == "authData") authData = reader.ReadByteString(); else if (key == "attStmt") { reader.ReadStartMap(); while (reader.PeekState() != CborReaderState.EndMap) { var attKey = reader.ReadTextString(); if (attKey == "x5c") { var arrayLength = reader.ReadStartArray(); certificates = new(); for (var j = 0; arrayLength is null || j < arrayLength; j++) certificates.Add(reader.ReadByteString()); reader.ReadEndArray(); } else reader.SkipValue(); } reader.ReadEndMap(); } else reader.SkipValue(); }
        reader.ReadEndMap();
        if (authData is null || certificates is null || certificates.Count < 2) throw new InvalidOperationException("Malformed Apple attestation object.");
        var leaf = X509CertificateLoader.LoadCertificate(certificates[0]); ValidateAppleCertificateChain(leaf, certificates.Skip(1));
        var nonceHash = SHA256.HashData(authData.Concat(SHA256.HashData(Encoding.UTF8.GetBytes(nonce))).ToArray());
        var extension = leaf.Extensions["1.2.840.113635.100.8.2"] ?? throw new InvalidOperationException("Apple attestation nonce extension is missing.");
        var extensionReader = new AsnReader(extension.RawData, AsnEncodingRules.DER);
        var sequence = extensionReader.ReadSequence();
        var certificateNonce = sequence.ReadOctetString();
        sequence.ThrowIfNotEmpty();
        extensionReader.ThrowIfNotEmpty();
        if (!CryptographicOperations.FixedTimeEquals(certificateNonce, nonceHash)) throw new InvalidOperationException("Apple attestation nonce is invalid.");
        return (authData, leaf);
    }

    private void ValidateAppleCertificateChain(X509Certificate2 leaf, IEnumerable<byte[]> intermediates)
    {
        var rootPem = GetRequired("AppAttestation:iOS:RootCertificatePem");
        using var root = X509Certificate2.CreateFromPem(rootPem); using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust; chain.ChainPolicy.CustomTrustStore.Add(root);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.Online;
        foreach (var intermediate in intermediates) chain.ChainPolicy.ExtraStore.Add(X509CertificateLoader.LoadCertificate(intermediate));
        if (!chain.Build(leaf)) throw new InvalidOperationException("Apple attestation certificate chain is invalid.");
    }

    private void ValidateAppleAuthenticatorData(byte[] authData, bool isAssertion, out long counter)
    {
        if (authData.Length < 37) throw new InvalidOperationException("Invalid App Attest authenticator data.");
        var appIdHash = SHA256.HashData(Encoding.UTF8.GetBytes($"{GetRequired("AppAttestation:iOS:TeamId")}.{GetRequired("AppAttestation:iOS:BundleId")}"));
        if (!CryptographicOperations.FixedTimeEquals(authData.AsSpan(0, 32), appIdHash)) throw new InvalidOperationException("App Attest app identifier mismatch.");
        counter = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(authData.AsSpan(33, 4));
        if (!isAssertion && (authData[32] & 0x40) == 0) throw new InvalidOperationException("App Attest credential data is missing.");
    }

    private static ECDsa ExtractAppleCredentialPublicKey(byte[] authData, string keyId)
    {
        if (authData.Length < 55) throw new InvalidOperationException("App Attest credential data is malformed.");
        var credentialIdLength = System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(authData.AsSpan(53, 2));
        var coseStart = 55 + credentialIdLength;
        if (coseStart >= authData.Length || !FixedTimeEquals(Base64Url(authData.AsSpan(55, credentialIdLength).ToArray()), keyId))
            throw new InvalidOperationException("App Attest key identifier does not match credential data.");
        var reader = new CborReader(authData[coseStart..], CborConformanceMode.Ctap2Canonical);
        var count = reader.ReadStartMap(); byte[]? x = null; byte[]? y = null; int? curve = null;
        for (var i = 0; count is null || i < count; i++)
        {
            var label = reader.ReadInt32();
            switch (label) { case -1: curve = reader.ReadInt32(); break; case -2: x = reader.ReadByteString(); break; case -3: y = reader.ReadByteString(); break; default: reader.SkipValue(); break; }
        }
        reader.ReadEndMap();
        if (curve != 1 || x?.Length != 32 || y?.Length != 32) throw new InvalidOperationException("Unsupported App Attest credential public key.");
        return ECDsa.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = new ECPoint { X = x, Y = y } });
    }

    private string GetRequired(string key) => _configuration[key] is { Length: > 0 } value ? value : throw new InvalidOperationException($"{key} configuration is missing.");
    private static byte[] DecodeBase64Url(string value) => Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/').PadRight(value.Length + (4 - value.Length % 4) % 4, '='));
    private static string Base64Url(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static bool FixedTimeEquals(string expected, string? actual) => actual is not null && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(actual));
}
